using Microsoft.EntityFrameworkCore;

namespace RelayLab.Core;

public sealed record ReplayResult(Delivery? Delivery, ReplayRequest? Receipt, bool Created);

public sealed class Replay(IDbContextFactory<RelayDb> databases)
{
    public async Task<ReplayResult> ReplayAsync(Guid id, string key, CancellationToken ct)
    {
        await using var db = await databases.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Serialize replay decisions on this one delivery, including the receipt lookup.
        var delivery = await db.Deliveries.FromSqlInterpolated($"SELECT * FROM Deliveries WITH (UPDLOCK, ROWLOCK) WHERE Id={id}")
            .SingleOrDefaultAsync(ct);
        if (delivery is null) return new(null, null, false);
        var prior = await db.ReplayRequests.SingleOrDefaultAsync(r => r.DeliveryId == id && r.Key == key, ct);
        if (prior is not null) return new(delivery, prior, false);
        if (delivery.Status != "Failed") return new(delivery, null, false);
        using var activity = Telemetry.Start("replay", delivery);
        var now = await db.UtcNowAsync(ct);
        delivery.Generation++;
        delivery.AttemptNumber = 1;
        delivery.WorkId = Guid.NewGuid();
        delivery.Status = "Pending";
        delivery.NextAttemptUtc = now;
        delivery.UpdatedUtc = now;
        delivery.LastOutcome = "ReplayScheduled";
        // Preserve remote knowledge from the previous generation until a new attempt starts.
        var receipt = new ReplayRequest { DeliveryId = id, Key = key, Generation = delivery.Generation, WorkId = delivery.WorkId, CreatedUtc = now };
        db.ReplayRequests.Add(receipt);
        await db.SaveChangesAsync(ct);
        db.OutboxMessages.Add(new OutboxMessage { Id = delivery.WorkId, DeliveryId = id, Generation = delivery.Generation, CreatedUtc = now, NextPublishUtc = now });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        activity?.SetTag("relaylab.generation", delivery.Generation);
        activity?.SetTag("relaylab.work.id", delivery.WorkId.ToString("D"));
        activity?.SetTag("relaylab.attempt.number", delivery.AttemptNumber);
        Telemetry.Replays.Add(1);
        return new(delivery, receipt, true);
    }
}
