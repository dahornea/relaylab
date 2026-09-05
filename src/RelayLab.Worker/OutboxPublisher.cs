using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RelayLab.Core;

namespace RelayLab.Worker;

public sealed class OutboxPublisher(IDbContextFactory<RelayDb> databases, ServiceBusSender sender, WorkerSettings settings, WorkerBoundary boundary)
{
    public async Task<bool> PublishOneAsync(CancellationToken ct)
    {
        await using var db = await databases.CreateDbContextAsync(ct);
        var candidate = await db.OutboxMessages.FromSqlRaw("""
            SELECT TOP(1) o.* FROM OutboxMessages o JOIN Deliveries d ON d.WorkId=o.Id AND d.Id=o.DeliveryId
            WHERE d.Status='Pending' AND d.NextAttemptUtc <= SYSUTCDATETIME() AND o.NextPublishUtc <= SYSUTCDATETIME()
              AND (o.LeaseUntilUtc IS NULL OR o.LeaseUntilUtc <= SYSUTCDATETIME())
            ORDER BY o.NextPublishUtc, o.Id
            """).AsNoTracking().FirstOrDefaultAsync(ct);
        if (candidate is null) return false;
        var owner = Guid.NewGuid();
        var claimed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE OutboxMessages SET LeaseOwner={owner}, LeaseUntilUtc=DATEADD(second, 30, SYSUTCDATETIME()), SendCount=SendCount+1
            WHERE Id={candidate.Id} AND NextPublishUtc <= SYSUTCDATETIME()
              AND (LeaseUntilUtc IS NULL OR LeaseUntilUtc <= SYSUTCDATETIME())
            """, ct);
        if (claimed != 1) return true;
        var delivery = await db.Deliveries.AsNoTracking().SingleAsync(d => d.Id == candidate.DeliveryId, ct);
        using var activity = Telemetry.Start("publish", delivery, System.Diagnostics.ActivityKind.Producer);
        activity?.SetTag("relaylab.work.id", candidate.Id.ToString("D"));
        try
        {
            // No SQL transaction is held across Service Bus. Ambiguous sends reuse this MessageId.
            var body = JsonSerializer.Serialize(new WorkEnvelope(1, candidate.DeliveryId, candidate.Id), EventContract.Json);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            await sender.SendMessageAsync(new ServiceBusMessage(body)
            {
                MessageId = candidate.Id.ToString("D"), ContentType = "application/json", TimeToLive = TimeSpan.FromHours(1)
            }, deadline.Token);
            Telemetry.Publications.Add(1, new KeyValuePair<string, object?>("outcome", "sent"));
            await boundary.HitAsync("AfterPublish", candidate.Id, ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE OutboxMessages SET PublishedUtc=SYSUTCDATETIME(), LeaseOwner=NULL, LeaseUntilUtc=NULL, LastError=NULL,
                    NextPublishUtc=DATEADD(second, {settings.ReconcileSeconds}, SYSUTCDATETIME())
                WHERE Id={candidate.Id} AND LeaseOwner={owner} AND LeaseUntilUtc > SYSUTCDATETIME()
                """, ct);
        }
        catch (Exception error) when (error is ServiceBusException or OperationCanceledException)
        {
            ct.ThrowIfCancellationRequested();
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, "BrokerUnavailable");
            Telemetry.Publications.Add(1, new KeyValuePair<string, object?>("outcome", "unavailable"));
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE OutboxMessages SET LeaseOwner=NULL, LeaseUntilUtc=NULL, LastError='BrokerUnavailable',
                    NextPublishUtc=DATEADD(second, {settings.PublishRetrySeconds}, SYSUTCDATETIME())
                WHERE Id={candidate.Id} AND LeaseOwner={owner} AND LeaseUntilUtc > SYSUTCDATETIME()
                """, ct);
        }
        return true;
    }
}

public sealed class PublishingService(OutboxPublisher publisher, DeliveryTransitions transitions, ILogger<PublishingService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                for (var i = 0; i < 20 && await transitions.RecoverOneAsync(stoppingToken); i++) { }
                for (var i = 0; i < 20 && await publisher.PublishOneAsync(stoppingToken); i++) { }
                await transitions.ObserveBacklogAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception)
            {
                logger.LogWarning("Outbox iteration failed; durable work and expiring claims remain available.");
            }
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }
}
