using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RelayLab.Core;

namespace RelayLab.Api;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var initialize = args.Contains("--init-db");
        await using var app = Build(args.Where(a => a != "--init-db").ToArray());
        if (initialize)
        {
            await using var db = await app.Services.GetRequiredService<IDbContextFactory<RelayDb>>().CreateDbContextAsync();
            await db.Database.EnsureCreatedAsync();
            return;
        }
        await app.RunAsync();
    }

    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        configure?.Invoke(builder);
        LocalHosting.ConfigureWeb(builder);
        builder.Services.AddDbContextFactory<RelayDb>(options => options.UseSqlServer(LocalHosting.Connection(builder.Configuration, "RelayLab")));
        builder.Services.AddTransient<Acceptance>();
        var app = builder.Build();
        LocalHosting.UseSafeErrors(app);

        app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
        app.MapGet("/health/ready", async (IDbContextFactory<RelayDb> databases, CancellationToken ct) =>
        {
            await using var db = await databases.CreateDbContextAsync(ct);
            _ = await db.Deliveries.AnyAsync(ct);
            return Results.Ok(new { status = "ready" });
        });

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

        app.MapGet("/deliveries/{id}", async (string id, IDbContextFactory<RelayDb> databases, CancellationToken ct) =>
        {
            if (!Guid.TryParse(id, out var deliveryId))
                return LocalHosting.Problem(400, "invalid_delivery_id", "Use a UUID delivery identifier.");
            await using var db = await databases.CreateDbContextAsync(ct);
            var delivery = await db.Deliveries.AsNoTracking().SingleOrDefaultAsync(d => d.Id == deliveryId, ct);
            if (delivery is null) return LocalHosting.Problem(404, "delivery_not_found", "Delivery not found.");
            var now = await db.UtcNowAsync(ct);
            var attempts = await db.DeliveryAttempts.AsNoTracking().Where(a => a.DeliveryId == deliveryId)
                .OrderByDescending(a => a.StartedUtc).ThenBy(a => a.Id).Take(50).ToListAsync(ct);
            var count = await db.DeliveryAttempts.CountAsync(a => a.DeliveryId == deliveryId, ct);
            var outbox = await db.OutboxMessages.AsNoTracking().SingleAsync(o => o.Id == delivery.WorkId, ct);
            var expired = delivery.Status == "Processing" && delivery.LeaseUntilUtc <= now;
            var incomplete = delivery.Status is "Pending" or "Processing";
            return Results.Ok(new
            {
                deliveryId, delivery.Status,
                acceptedUtc = AsUtc(delivery.AcceptedUtc), updatedUtc = AsUtc(delivery.UpdatedUtc),
                delivery.LastOutcome, delivery.RemoteOutcome,
                leaseExpiresUtc = AsUtc(delivery.LeaseUntilUtc), leaseExpired = expired,
                warning = expired ? "Interrupted attempt; remote outcome unknown. Await redelivery and inspect the dead-letter queue."
                    : incomplete && outbox.PublishedUtc is not null ? "Published work has no completed outcome. Broker state is not reconciled; inspect the dead-letter queue if it remains incomplete." : null,
                publication = new { publishedUtc = AsUtc(outbox.PublishedUtc), outbox.SendCount, outbox.LastError },
                attemptCount = count, historyTruncated = count > attempts.Count,
                attempts = attempts.Select(a => new
                {
                    attemptId = a.Id, startedUtc = AsUtc(a.StartedUtc), completedUtc = AsUtc(a.CompletedUtc),
                    a.Outcome, a.HttpStatus, a.FailureCategory, a.RemoteOutcome
                })
            });
        });
        return app;
    }

    private static DateTime? AsUtc(DateTime? date) => date is { } value ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : null;
}
