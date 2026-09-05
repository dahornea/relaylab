using Microsoft.EntityFrameworkCore;
using RelayLab.Core;

namespace RelayLab.Receiver;

public static class Program
{
    public static async Task Main(string[] args)
    {
        if (args.Contains("--deploy-schema"))
        {
            var job = WebApplication.CreateBuilder([]);
            await using var database = new ReceiverDb(new DbContextOptionsBuilder<ReceiverDb>()
                .UseSqlServer(CloudHosting.SqlConnection(job.Configuration, "Receiver", true)).Options);
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            await SchemaDeployment.ApplyAsync(database, "receiver", deadline.Token);
            await SchemaDeployment.GrantRuntimeAsync(database, job.Configuration, true, deadline.Token);
            Console.WriteLine("Receiver schema baseline and runtime grants verified.");
            return;
        }
        var initialize = args.Contains("--init-db");
        await using var app = Build(args.Where(a => a != "--init-db").ToArray());
        if (initialize)
        {
            if (CloudHosting.IsCloud(app.Environment)) throw new InvalidOperationException("Production initialization requires the explicit schema deployment job.");
            await using var db = await app.Services.GetRequiredService<IDbContextFactory<ReceiverDb>>().CreateDbContextAsync();
            await db.Database.EnsureCreatedAsync();
            return;
        }
        if (CloudHosting.IsCloud(app.Environment))
        {
            await using var db = await app.Services.GetRequiredService<IDbContextFactory<ReceiverDb>>().CreateDbContextAsync();
            await SchemaDeployment.VerifyAsync(db, "receiver", default);
        }
        await app.RunAsync();
    }

    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        configure?.Invoke(builder);
        LocalHosting.ConfigureWeb(builder);
        Telemetry.Configure(builder.Services, builder.Configuration, "relaylab-receiver");
        // This separate local sample is the only executable with operator-controlled fault behavior.
        var mode = builder.Configuration["Receiver:Mode"] ?? "Acknowledge";
        if (mode is not ("Acknowledge" or "Reject" or "CommitThenAbortOnce" or "CommitThenWaitOnce"))
            throw new InvalidOperationException("Unknown local receiver mode.");
        builder.Services.AddDbContextFactory<ReceiverDb>(o => o.UseSqlServer(CloudHosting.SqlConnection(builder.Configuration, "Receiver", CloudHosting.IsCloud(builder.Environment))));
        builder.Services.AddTransient<ReceiverLedger>();
        var app = builder.Build();
        LocalHosting.UseSafeErrors(app);
        CloudHosting.UseAuthentication(app);
        app.MapGet("/health/live", () => Results.Ok(new { status = "live", schemaVersion = SchemaDeployment.Version })).AllowAnonymous();
        app.MapGet("/health/ready", async (IDbContextFactory<ReceiverDb> databases, CancellationToken ct) =>
        {
            await using var db = await databases.CreateDbContextAsync(ct);
            _ = await db.Receipts.AnyAsync(ct);
            return Results.Ok(new { status = "ready" });
        }).AllowAnonymous();
        app.MapPost("/webhooks", async (HttpRequest http, ReceiverLedger ledger, CancellationToken ct) =>
        {
            System.Diagnostics.ActivityContext.TryParse(http.Headers["traceparent"].FirstOrDefault(), null, out var parent);
            using var activity = Telemetry.Activities.StartActivity("receive", System.Diagnostics.ActivityKind.Server, parent);
            var ids = http.Headers["X-RelayLab-Delivery-Id"];
            if (ids.Count != 1 || !Guid.TryParse(ids[0], out var id) || id == Guid.Empty)
                return LocalHosting.Problem(400, "invalid_delivery_id", "Supply X-RelayLab-Delivery-Id as a nonempty UUID.");
            var (request, error) = await LocalHosting.ReadEventAsync(http, ct);
            if (error is not null) return error;
            var errors = EventContract.Validate(request, requireKey: false);
            if (errors.Count > 0) return LocalHosting.Validation(errors);
            activity?.SetTag("relaylab.delivery.id", id.ToString("D"));
            if (mode == "Reject") return Results.StatusCode(503);
            var receipt = await ledger.RecordDetailedAsync(id, request!, ct);
            if (!receipt.Matches) return LocalHosting.Problem(409, "receipt_conflict", "This delivery ID was recorded with different content.");
            if (receipt.Created && mode == "CommitThenWaitOnce")
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            if (receipt.Created && mode == "CommitThenAbortOnce")
            {
                http.HttpContext.Abort();
                return Results.Empty;
            }
            return Results.Ok(new { deliveryId = id, acknowledged = true });
        });
        // Local sample diagnostic endpoint. The delivery service never calls this.
        var receipts = app.MapGet("/receipts/{id:guid}", async (Guid id, IDbContextFactory<ReceiverDb> databases, CancellationToken ct) =>
        {
            await using var db = await databases.CreateDbContextAsync(ct);
            if (!await db.Receipts.AnyAsync(r => r.DeliveryId == id, ct))
                return LocalHosting.Problem(404, "receipt_not_found", "Receipt not found.");
            return Results.Ok(new { deliveryId = id, effectCount = await db.Effects.CountAsync(e => e.DeliveryId == id, ct) });
        });
        if (CloudHosting.IsCloud(app.Environment)) receipts.RequireAuthorization(CloudHosting.OperatorPolicy);
        return app;
    }
}
