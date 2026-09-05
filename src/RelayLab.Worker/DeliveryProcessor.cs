using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using RelayLab.Core;

namespace RelayLab.Worker;

public sealed class DeliveryProcessor(IDbContextFactory<RelayDb> databases, HttpClient http, WorkerSettings settings)
{
    // null means complete; a safe reason means dead-letter. Exceptions leave the message unsettled.
    public async Task<string?> ProcessAsync(WorkEnvelope work, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            await using var db = await databases.CreateDbContextAsync(ct);
            var delivery = await db.Deliveries.AsNoTracking().SingleOrDefaultAsync(d => d.Id == work.DeliveryId, ct);
            if (delivery is null) return "UnknownDelivery";
            if (delivery.WorkId != work.WorkId) return "StaleWork";
            if (delivery.Status is "Delivered" or "Failed") return null;
            var owner = Guid.NewGuid();
            var attemptId = Guid.NewGuid();
            await using (var transaction = await db.Database.BeginTransactionAsync(ct))
            {
                var claimed = await db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE Deliveries SET Status='Processing', LeaseOwner={owner},
                        LeaseUntilUtc=DATEADD(second, {settings.LeaseSeconds}, SYSUTCDATETIME()), UpdatedUtc=SYSUTCDATETIME(),
                        LastOutcome='HttpInProgress', RemoteOutcome='Unknown'
                    WHERE Id={work.DeliveryId} AND WorkId={work.WorkId}
                        AND (Status='Pending' OR (Status='Processing' AND LeaseUntilUtc <= SYSUTCDATETIME()))
                    """, ct);
                if (claimed != 1)
                {
                    await transaction.RollbackAsync(ct);
                    // Wait under the broker lock, avoiding rapid abandon/redelivery exhaustion for a busy lease.
                    await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
                    continue;
                }
                await db.DeliveryAttempts.Where(a => a.DeliveryId == work.DeliveryId && a.CompletedUtc == null)
                    .ExecuteUpdateAsync(u => u.SetProperty(a => a.Outcome, "Interrupted")
                        .SetProperty(a => a.FailureCategory, "LeaseExpired").SetProperty(a => a.RemoteOutcome, "Unknown"), ct);
                db.DeliveryAttempts.Add(new DeliveryAttempt
                {
                    Id = attemptId, DeliveryId = work.DeliveryId, WorkId = work.WorkId, StartedUtc = await db.UtcNowAsync(ct)
                });
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
            }

            // The durable Started record exists before HTTP; no SQL transaction crosses this boundary.
            var result = await SendAsync(delivery, ct);
            await using var commit = await db.Database.BeginTransactionAsync(ct);
            var changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE Deliveries SET Status={result.SuccessStatus}, UpdatedUtc=SYSUTCDATETIME(),
                    LastOutcome={result.Outcome}, RemoteOutcome={result.RemoteOutcome}, LeaseOwner=NULL, LeaseUntilUtc=NULL
                WHERE Id={work.DeliveryId} AND WorkId={work.WorkId} AND Status='Processing'
                    AND LeaseOwner={owner} AND LeaseUntilUtc > SYSUTCDATETIME()
                """, ct);
            if (changed != 1)
            {
                await commit.RollbackAsync(ct);
                throw new InvalidOperationException("The attempt lease expired before its result could be persisted.");
            }
            var now = await db.UtcNowAsync(ct);
            await db.DeliveryAttempts.Where(a => a.Id == attemptId).ExecuteUpdateAsync(u =>
                u.SetProperty(a => a.CompletedUtc, now).SetProperty(a => a.Outcome, result.Outcome)
                    .SetProperty(a => a.HttpStatus, result.HttpStatus).SetProperty(a => a.FailureCategory, result.FailureCategory)
                    .SetProperty(a => a.RemoteOutcome, result.RemoteOutcome), ct);
            await commit.CommitAsync(ct);
            return null;
        }
    }

    private async Task<HttpOutcome> SendAsync(Delivery delivery, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(settings.HttpTimeoutSeconds));
        using var request = new HttpRequestMessage(HttpMethod.Post, settings.Destination)
        {
            Content = JsonContent.Create(delivery.ToRequest(), options: EventContract.Json)
        };
        request.Headers.Add("X-RelayLab-Delivery-Id", delivery.Id.ToString("D"));
        try
        {
            // Only the status/headers matter. No response body is read, buffered or drained.
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            return response.IsSuccessStatusCode
                ? new("Delivered", "Acknowledged", (int)response.StatusCode, null, "Acknowledged")
                : new("Failed", "HttpRejected", (int)response.StatusCode, "NonSuccessStatus", "ResponseReceived");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new("Failed", "Timeout", null, "DeadlineExceeded", "Unknown");
        }
        catch (HttpRequestException)
        {
            return new("Failed", "TransportFailure", null, "HttpTransport", "Unknown");
        }
    }

    private sealed record HttpOutcome(string SuccessStatus, string Outcome, int? HttpStatus, string? FailureCategory, string RemoteOutcome);
}
