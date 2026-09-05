using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RelayLab.Core;

namespace RelayLab.Worker;

public sealed class ConsumingService(ServiceBusClient bus, DeliveryProcessor deliveries, WorkerSettings settings,
    ILogger<ConsumingService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var processor = bus.CreateProcessor(settings.Queue, new ServiceBusProcessorOptions
        {
            ReceiveMode = ServiceBusReceiveMode.PeekLock, AutoCompleteMessages = false, PrefetchCount = 0,
            MaxConcurrentCalls = settings.MaxConcurrentCalls, MaxAutoLockRenewalDuration = TimeSpan.FromMinutes(2)
        });
        processor.ProcessErrorAsync += _ =>
        {
            logger.LogWarning("Broker processing error; check connectivity and the dead-letter queue.");
            return Task.CompletedTask;
        };
        processor.ProcessMessageAsync += async args =>
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, args.CancellationToken);
            var ct = cancellation.Token;
            WorkEnvelope? work = null;
            try
            {
                if (args.Message.Body.ToMemory().Length <= EventContract.BodyLimit)
                    work = JsonSerializer.Deserialize<WorkEnvelope>(args.Message.Body.ToMemory().Span, EventContract.Json);
            }
            catch (JsonException) { }
            if (work is null || work.Version != 1 || work.DeliveryId == Guid.Empty || work.WorkId == Guid.Empty ||
                args.Message.MessageId != work.WorkId.ToString("D"))
            {
                await args.DeadLetterMessageAsync(args.Message, "InvalidEnvelope", "Unsupported or invalid work identity.", ct);
                return;
            }
            var reason = await deliveries.ProcessAsync(work, ct);
            if (reason is not null)
                await args.DeadLetterMessageAsync(args.Message, reason, "Work does not match a current delivery.", ct);
            else
                await args.CompleteMessageAsync(args.Message, ct); // Durable state precedes settlement.
        };
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await processor.StartProcessingAsync(stoppingToken);
                break;
            }
            catch (ServiceBusException)
            {
                logger.LogWarning("Waiting for the broker to become available.");
                await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            }
        }
        try { await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        await processor.StopProcessingAsync(CancellationToken.None);
    }
}
