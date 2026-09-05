using Microsoft.EntityFrameworkCore;
using RelayLab.Core;

namespace RelayLab.Receiver;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var initialize = args.Contains("--init-db");
        await using var app = Build(args.Where(a => a != "--init-db").ToArray());
        if (initialize)
        {
            await using var db = await app.Services.GetRequiredService<IDbContextFactory<ReceiverDb>>().CreateDbContextAsync();
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
        builder.Services.AddDbContextFactory<ReceiverDb>(o => o.UseSqlServer(LocalHosting.Connection(builder.Configuration, "Receiver")));
        builder.Services.AddTransient<ReceiverLedger>();
        var app = builder.Build();
        LocalHosting.UseSafeErrors(app);
        app.MapGet("/health/ready", async (IDbContextFactory<ReceiverDb> databases, CancellationToken ct) =>
        {
            await using var db = await databases.CreateDbContextAsync(ct);
            _ = await db.Receipts.AnyAsync(ct);
            return Results.Ok(new { status = "ready" });
        });
        app.MapPost("/webhooks", async (HttpRequest http, ReceiverLedger ledger, CancellationToken ct) =>
        {
            var ids = http.Headers["X-RelayLab-Delivery-Id"];
            if (ids.Count != 1 || !Guid.TryParse(ids[0], out var id) || id == Guid.Empty)
                return LocalHosting.Problem(400, "invalid_delivery_id", "Supply X-RelayLab-Delivery-Id as a nonempty UUID.");
            var (request, error) = await LocalHosting.ReadEventAsync(http, ct);
            if (error is not null) return error;
            var errors = EventContract.Validate(request, requireKey: false);
            if (errors.Count > 0) return LocalHosting.Validation(errors);
            return await ledger.RecordAsync(id, request!, ct)
                ? Results.Ok(new { deliveryId = id, acknowledged = true })
                : LocalHosting.Problem(409, "receipt_conflict", "This delivery ID was recorded with different content.");
        });
        // Local sample diagnostic endpoint. The delivery service never calls this.
        app.MapGet("/receipts/{id:guid}", async (Guid id, IDbContextFactory<ReceiverDb> databases, CancellationToken ct) =>
        {
            await using var db = await databases.CreateDbContextAsync(ct);
            if (!await db.Receipts.AnyAsync(r => r.DeliveryId == id, ct))
                return LocalHosting.Problem(404, "receipt_not_found", "Receipt not found.");
            return Results.Ok(new { deliveryId = id, effectCount = await db.Effects.CountAsync(e => e.DeliveryId == id, ct) });
        });
        return app;
    }
}
