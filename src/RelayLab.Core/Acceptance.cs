using Microsoft.EntityFrameworkCore;

namespace RelayLab.Core;

public sealed record AcceptanceResult(Delivery Delivery, bool Created, bool Conflict);

public sealed class Acceptance(IDbContextFactory<RelayDb> databases, RetryPolicy policy)
{
    public async Task<AcceptanceResult> AcceptAsync(string key, EventRequest request, CancellationToken ct)
    {
        using var activity = Telemetry.Activities.StartActivity("accept");
        await using var db = await databases.CreateDbContextAsync(ct);
        var existing = await db.Deliveries.AsNoTracking().SingleOrDefaultAsync(d => d.IdempotencyKey == key, ct);
        if (existing is not null)
            return Existing(existing, request);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var now = await db.UtcNowAsync(ct);
        var delivery = new Delivery
        {
            Id = Guid.NewGuid(), WorkId = Guid.NewGuid(), IdempotencyKey = key,
            DestinationId = request.DestinationId!, EventType = request.EventType!, DocumentId = request.Data!.DocumentId!,
            AcceptedUtc = now, UpdatedUtc = now, NextAttemptUtc = now,
            MaxAttempts = policy.MaxAttempts, RetryBaseSeconds = policy.BaseSeconds, TraceParent = activity?.Id
        };
        try
        {
            db.Deliveries.Add(delivery);
            await db.SaveChangesAsync(ct);
            db.OutboxMessages.Add(new OutboxMessage
            {
                Id = delivery.WorkId, DeliveryId = delivery.Id, CreatedUtc = now, NextPublishUtc = now
            });
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            activity?.SetTag("relaylab.delivery.id", delivery.Id.ToString("D"));
            Telemetry.Accepted.Add(1);
            return new(delivery, true, false);
        }
        catch (DbUpdateException error) when (SqlErrors.IsUniqueViolation(error))
        {
            await transaction.RollbackAsync(ct);
            await using var fresh = await databases.CreateDbContextAsync(ct);
            existing = await fresh.Deliveries.AsNoTracking().SingleOrDefaultAsync(d => d.IdempotencyKey == key, ct);
            if (existing is null) throw;
            return Existing(existing, request);
        }
    }

    private static AcceptanceResult Existing(Delivery delivery, EventRequest request) =>
        new(delivery, false, !EventContract.Matches(request, delivery.DestinationId, delivery.EventType, delivery.DocumentId));
}
