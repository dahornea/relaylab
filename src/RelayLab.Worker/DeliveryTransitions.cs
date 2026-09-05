using Microsoft.EntityFrameworkCore;
using RelayLab.Core;

namespace RelayLab.Worker;

public sealed record HttpOutcome(bool Success, bool Retryable, string Outcome, int? HttpStatus, string? FailureCategory, string RemoteOutcome);

public sealed class DeliveryTransitions(IDbContextFactory<RelayDb> databases)
{
    public async Task ObserveBacklogAsync(CancellationToken ct)
    {
        await using var db = await databases.CreateDbContextAsync(ct);
        Telemetry.ObservePending(await db.Deliveries.LongCountAsync(d => d.Status == "Pending", ct));
    }

    public async Task<bool> FinishAsync(Delivery delivery, Guid owner, HttpOutcome result, bool interrupted, CancellationToken ct)
    {
        await using var db = await databases.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var retry = !result.Success && result.Retryable && delivery.AttemptNumber < delivery.MaxAttempts;
        var nextWork = retry ? Guid.NewGuid() : delivery.WorkId;
        var nextNumber = delivery.AttemptNumber + (retry ? 1 : 0);
        var status = result.Success ? "Delivered" : retry ? "Pending" : "Failed";
        var last = result.Success ? result.Outcome : retry ? "RetryScheduled" : result.Retryable ? "RetryExhausted" : "NonRetryableFailure";
        var now = await db.UtcNowAsync(ct);
        var nextDue = retry ? now.AddSeconds(RetryPolicy.DelaySeconds(delivery)) : delivery.NextAttemptUtc;
        // Both the delivery and attempt changes are fenced by owner, work identity and SQL lease time.
        var changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE Deliveries SET Status={status}, WorkId={nextWork}, AttemptNumber={nextNumber},
                NextAttemptUtc={nextDue}, UpdatedUtc=SYSUTCDATETIME(), LastOutcome={last}, RemoteOutcome={result.RemoteOutcome},
                LeaseOwner=NULL, LeaseUntilUtc=NULL
            WHERE Id={delivery.Id} AND WorkId={delivery.WorkId} AND Status='Processing' AND LeaseOwner={owner}
              AND (({interrupted}=1 AND LeaseUntilUtc <= SYSUTCDATETIME()) OR ({interrupted}=0 AND LeaseUntilUtc > SYSUTCDATETIME()))
            """, ct);
        if (changed != 1) return false;
        var updated = await db.DeliveryAttempts.Where(a => a.WorkId == delivery.WorkId && a.Outcome == "Started")
            .ExecuteUpdateAsync(u => u.SetProperty(a => a.CompletedUtc, interrupted ? (DateTime?)null : now)
                .SetProperty(a => a.Outcome, result.Outcome).SetProperty(a => a.HttpStatus, result.HttpStatus)
                .SetProperty(a => a.FailureCategory, result.FailureCategory).SetProperty(a => a.RemoteOutcome, result.RemoteOutcome), ct);
        if (updated != 1) throw new InvalidOperationException("Claim has no unique Started attempt.");
        if (retry)
        {
            db.OutboxMessages.Add(new OutboxMessage
            {
                Id = nextWork, DeliveryId = delivery.Id, Generation = delivery.Generation, AttemptNumber = nextNumber,
                CreatedUtc = now, NextPublishUtc = nextDue
            });
            await db.SaveChangesAsync(ct);
        }
        await transaction.CommitAsync(ct);
        using var activity = Telemetry.Start(interrupted ? "recover" : "transition", delivery);
        activity?.SetTag("relaylab.outcome", result.Outcome);
        activity?.SetTag("relaylab.status", status);
        Telemetry.Outcomes.Add(1, new KeyValuePair<string, object?>("outcome", result.Outcome));
        return true;
    }

    public async Task<bool> RecoverOneAsync(CancellationToken ct)
    {
        await using var db = await databases.CreateDbContextAsync(ct);
        var delivery = await db.Deliveries.FromSqlRaw("""
            SELECT TOP(1) * FROM Deliveries WHERE Status='Processing' AND LeaseUntilUtc <= SYSUTCDATETIME()
            ORDER BY LeaseUntilUtc, Id
            """).AsNoTracking().FirstOrDefaultAsync(ct);
        if (delivery is null) return false;
        await FinishAsync(delivery, delivery.LeaseOwner!.Value,
            new(false, true, "Interrupted", null, "LeaseExpired", "Unknown"), true, ct);
        return true;
    }
}
