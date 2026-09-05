using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RelayLab.Core;
using Microsoft.AspNetCore.Authorization;

namespace RelayLab.Api;

public static class Program
{
    public static async Task Main(string[] args)
    {
        if (args.Contains("--deploy-schema"))
        {
            var job = WebApplication.CreateBuilder([]);
            await using var database = new RelayDb(new DbContextOptionsBuilder<RelayDb>()
                .UseSqlServer(CloudHosting.SqlConnection(job.Configuration, "RelayLab", true)).Options);
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            await SchemaDeployment.ApplyAsync(database, "relay", deadline.Token);
            await SchemaDeployment.GrantRuntimeAsync(database, job.Configuration, false, deadline.Token);
            Console.WriteLine("Relay schema baseline and runtime grants verified.");
            return;
        }
        var initialize = args.Contains("--init-db");
        await using var app = Build(args.Where(a => a != "--init-db").ToArray());
        if (initialize)
        {
            if (CloudHosting.IsCloud(app.Environment)) throw new InvalidOperationException("Production initialization requires the explicit schema deployment job.");
            await using var db = await app.Services.GetRequiredService<IDbContextFactory<RelayDb>>().CreateDbContextAsync();
            await db.Database.EnsureCreatedAsync();
            var currentSchema = await db.Database.SqlQueryRaw<bool>("""
                SELECT CAST(CASE WHEN COL_LENGTH('Deliveries', 'Generation') IS NOT NULL
                    AND OBJECT_ID('ReplayRequests') IS NOT NULL THEN 1 ELSE 0 END AS bit) AS [Value]
                """).SingleAsync();
            if (!currentSchema) throw new InvalidOperationException("A fresh M2 local database is required. Preserve existing M1 data in its own database; schema upgrades are not implemented.");
            return;
        }
        if (CloudHosting.IsCloud(app.Environment))
        {
            await using var db = await app.Services.GetRequiredService<IDbContextFactory<RelayDb>>().CreateDbContextAsync();
            await SchemaDeployment.VerifyAsync(db, "relay", default);
        }
        await app.RunAsync();
    }

    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        configure?.Invoke(builder);
        LocalHosting.ConfigureWeb(builder);
        Telemetry.Configure(builder.Services, builder.Configuration, "relaylab-api");
        builder.Services.AddDbContextFactory<RelayDb>(options => options.UseSqlServer(CloudHosting.SqlConnection(builder.Configuration, "RelayLab", CloudHosting.IsCloud(builder.Environment))));
        builder.Services.AddTransient<Acceptance>();
        builder.Services.AddTransient<Replay>();
        builder.Services.AddSingleton(RetryPolicy.From(builder.Configuration));
        var app = builder.Build();
        LocalHosting.UseSafeErrors(app);
        CloudHosting.UseAuthentication(app);

        app.MapGet("/health/live", () => Results.Ok(new { status = "live", schemaVersion = SchemaDeployment.Version })).AllowAnonymous();
        app.MapGet("/health/ready", async (IDbContextFactory<RelayDb> databases, CancellationToken ct) =>
        {
            await using var db = await databases.CreateDbContextAsync(ct);
            _ = await db.Deliveries.AnyAsync(ct);
            return Results.Ok(new { status = "ready" });
        }).AllowAnonymous();

        app.MapPost("/events", async (HttpRequest http, Acceptance acceptance, CancellationToken ct) =>
        {
            var (request, error) = await LocalHosting.ReadEventAsync(http, ct);
            if (error is not null) return error;
            var keys = http.Headers["Idempotency-Key"];
            var key = keys.Count == 1 ? keys[0] : null;
            var errors = EventContract.Validate(request, key);
            if (errors.Count > 0) return LocalHosting.Validation(errors);
            var result = await acceptance.AcceptAsync(key!, request!, ct);
            if (result.Conflict)
                return LocalHosting.Problem(409, "idempotency_conflict", "This key already identifies a different event.");
            var body = new { deliveryId = result.Delivery.Id, status = result.Delivery.Status };
            return result.Created ? Results.Accepted($"/deliveries/{result.Delivery.Id}", body) : Results.Ok(body);
        });

        app.MapPost("/deliveries/{id}/replay", async (string id, HttpRequest http, Replay replay, CancellationToken ct) =>
        {
            if (!Guid.TryParse(id, out var deliveryId))
                return LocalHosting.Problem(400, "invalid_delivery_id", "Use a UUID delivery identifier.");
            var keys = http.Headers["Idempotency-Key"];
            var key = keys.Count == 1 ? keys[0] : null;
            if (!EventContract.IsValidKey(key))
                return LocalHosting.Problem(400, "invalid_replay_key", "Supply one Idempotency-Key of 1-128 visible ASCII characters without whitespace.");
            if (await http.Body.ReadAsync(new byte[1], ct) != 0)
                return LocalHosting.Problem(400, "invalid_replay_body", "Replay has no request body.");
            var result = await replay.ReplayAsync(deliveryId, key!, ct);
            if (result.Delivery is null) return LocalHosting.Problem(404, "delivery_not_found", "Delivery not found.");
            if (result.Receipt is null) return LocalHosting.Problem(409, "replay_not_allowed", "Only a Failed delivery can start a new replay.");
            var body = new { deliveryId, result.Receipt.Generation, result.Receipt.WorkId, result.Delivery.Status };
            return result.Created ? Results.Accepted($"/deliveries/{deliveryId}", body) : Results.Ok(body);
        });

        app.MapGet("/deliveries/{id}", async (string id, HttpRequest http, IDbContextFactory<RelayDb> databases, CancellationToken ct) =>
        {
            if (!Guid.TryParse(id, out var deliveryId))
                return LocalHosting.Problem(400, "invalid_delivery_id", "Use a UUID delivery identifier.");
            if (!long.TryParse(http.Query["after"].FirstOrDefault() ?? "0", out var after) || after < 0 ||
                !int.TryParse(http.Query["limit"].FirstOrDefault() ?? "50", out var limit) || limit is < 1 or > 50)
                return LocalHosting.Problem(400, "invalid_cursor", "Use a nonnegative after cursor and limit 1-50.");
            await using var db = await databases.CreateDbContextAsync(ct);
            var delivery = await db.Deliveries.AsNoTracking().SingleOrDefaultAsync(d => d.Id == deliveryId, ct);
            if (delivery is null) return LocalHosting.Problem(404, "delivery_not_found", "Delivery not found.");
            var now = await db.UtcNowAsync(ct);
            var attempts = await db.DeliveryAttempts.AsNoTracking().Where(a => a.DeliveryId == deliveryId && a.Sequence > after)
                .OrderBy(a => a.Sequence).Take(limit + 1).ToListAsync(ct);
            var more = attempts.Count > limit;
            if (more) attempts.RemoveAt(limit);
            var count = await db.DeliveryAttempts.CountAsync(a => a.DeliveryId == deliveryId, ct);
            var outbox = await db.OutboxMessages.AsNoTracking().SingleAsync(o => o.Id == delivery.WorkId, ct);
            var expired = delivery.Status == "Processing" && delivery.LeaseUntilUtc <= now;
            return Results.Ok(new
            {
                deliveryId, delivery.Status, delivery.WorkId, delivery.Generation, delivery.AttemptNumber, delivery.MaxAttempts,
                remainingAttempts = delivery.Status is "Delivered" or "Failed" ? 0 : Math.Max(0, delivery.MaxAttempts - delivery.AttemptNumber + (delivery.Status == "Pending" ? 1 : 0)),
                nextAttemptUtc = delivery.Status == "Pending" ? AsUtc(delivery.NextAttemptUtc) : null,
                acceptedUtc = AsUtc(delivery.AcceptedUtc), updatedUtc = AsUtc(delivery.UpdatedUtc),
                delivery.LastOutcome, delivery.RemoteOutcome,
                leaseExpiresUtc = AsUtc(delivery.LeaseUntilUtc), leaseExpired = expired,
                warning = expired ? "Interrupted attempt; remote outcome unknown. SQL recovery will consume this slot and retry or exhaust its budget." : null,
                publication = new { publishedUtc = AsUtc(outbox.PublishedUtc), outbox.SendCount, outbox.LastError },
                attemptCount = count, historyTruncated = more, nextCursor = more ? (long?)attempts[^1].Sequence : null,
                attempts = attempts.Select(a => new
                {
                    attemptId = a.Id, a.Sequence, a.WorkId, a.Generation, a.AttemptNumber,
                    startedUtc = AsUtc(a.StartedUtc), completedUtc = AsUtc(a.CompletedUtc),
                    a.Outcome, a.HttpStatus, a.FailureCategory, a.RemoteOutcome
                })
            });
        });
        return app;
    }

    private static DateTime? AsUtc(DateTime? date) => date is { } value ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : null;
}
