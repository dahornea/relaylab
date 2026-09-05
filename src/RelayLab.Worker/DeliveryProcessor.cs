using System.Net.Http.Json;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using RelayLab.Core;

namespace RelayLab.Worker;

public sealed class DeliveryProcessor(IDbContextFactory<RelayDb> databases, HttpClient http, WorkerSettings settings,
    DeliveryTransitions transitions, WorkerBoundary boundary)
{
    // null means complete; SQL retains the recovery path for busy, early and stale signals.
    public async Task<string?> ProcessAsync(WorkEnvelope work, CancellationToken ct)
    {
        await using var db = await databases.CreateDbContextAsync(ct);
        var delivery = await db.Deliveries.AsNoTracking().SingleOrDefaultAsync(d => d.Id == work.DeliveryId, ct);
        if (delivery is null) return "UnknownDelivery";
        if (delivery.WorkId != work.WorkId || delivery.Status != "Pending") return null;
        var owner = Guid.NewGuid();
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            var claimed = await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE Deliveries SET Status='Processing', LeaseOwner={owner},
                    LeaseUntilUtc=DATEADD(second, {settings.LeaseSeconds}, SYSUTCDATETIME()), UpdatedUtc=SYSUTCDATETIME(),
                    LastOutcome='HttpInProgress', RemoteOutcome='Unknown'
                WHERE Id={work.DeliveryId} AND WorkId={work.WorkId} AND Status='Pending' AND NextAttemptUtc <= SYSUTCDATETIME()
                """, ct);
            if (claimed != 1) return null;
            db.DeliveryAttempts.Add(new DeliveryAttempt
            {
                Id = Guid.NewGuid(), DeliveryId = work.DeliveryId, WorkId = work.WorkId, Generation = delivery.Generation,
                AttemptNumber = delivery.AttemptNumber, StartedUtc = await db.UtcNowAsync(ct)
            });
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            Telemetry.Attempts.Add(1);
        }
        await boundary.HitAsync("AfterClaim", work.WorkId, ct);
        using var activity = Telemetry.Start("attempt", delivery, ActivityKind.Consumer);
        // No SQL transaction or held connection spans the outbound HTTP call.
        var result = await SendAsync(delivery, ct);
        activity?.SetTag("relaylab.outcome", result.Outcome);
        if (!result.Success) activity?.SetStatus(ActivityStatusCode.Error, result.Outcome);
        await boundary.HitAsync("AfterHttp", work.WorkId, ct);
        var committed = await transitions.FinishAsync(delivery, owner, result, false, ct);
        await boundary.HitAsync(committed ? "AfterTransition" : "StaleCompletion", work.WorkId, ct);
        return null;
    }

    private async Task<HttpOutcome> SendAsync(Delivery delivery, CancellationToken ct)
    {
        using var activity = Telemetry.Activities.StartActivity("webhook", ActivityKind.Client);
        var started = Stopwatch.GetTimestamp();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(settings.HttpTimeoutSeconds));
        using var request = new HttpRequestMessage(HttpMethod.Post, settings.Destination)
        {
            Content = JsonContent.Create(delivery.ToRequest(), options: EventContract.Json)
        };
        request.Headers.Add("X-RelayLab-Delivery-Id", delivery.Id.ToString("D"));
        if (activity?.Id is { } parent) request.Headers.TryAddWithoutValidation("traceparent", parent);
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            return response.IsSuccessStatusCode
                ? new(true, false, "Acknowledged", (int)response.StatusCode, null, "Acknowledged")
                : new(false, RetryPolicy.IsRetryable((int)response.StatusCode), "HttpRejected", (int)response.StatusCode, "NonSuccessStatus", "ResponseReceived");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(false, true, "Timeout", null, "DeadlineExceeded", "Unknown");
        }
        catch (HttpRequestException)
        {
            return new(false, true, "TransportFailure", null, "HttpTransport", "Unknown");
        }
        finally { Telemetry.HttpDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds); }
    }
}
