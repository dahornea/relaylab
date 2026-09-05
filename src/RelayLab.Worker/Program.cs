using Azure.Messaging.ServiceBus;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RelayLab.Core;

namespace RelayLab.Worker;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var inspect = args.Contains("--deadletters");
        using var host = Build(args.Where(a => a != "--deadletters").ToArray());
        if (inspect)
        {
            var settings = host.Services.GetRequiredService<WorkerSettings>();
            await using var receiver = host.Services.GetRequiredService<ServiceBusClient>().CreateReceiver(settings.Queue,
                new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter, ReceiveMode = ServiceBusReceiveMode.PeekLock });
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var messages = await receiver.PeekMessagesAsync(50, cancellationToken: timeout.Token);
            foreach (var message in messages)
            {
                // Only emit a validated ID and a fixed reason category; never untrusted message bodies/descriptions.
                var id = Guid.TryParse(message.MessageId, out var parsed) ? parsed.ToString("D") : "invalid";
                var reason = message.DeadLetterReason is "InvalidEnvelope" or "UnknownDelivery" or "StaleWork" or "MaxDeliveryCountExceeded" or "TTLExpiredException"
                    ? message.DeadLetterReason : "Other";
                Console.WriteLine($"workId={id} reason={reason} deliveryCount={message.DeliveryCount}");
            }
            Console.WriteLine($"Peeked {messages.Count} messages (first 50). No messages removed.");
            return;
        }
        await host.RunAsync();
    }

    public static IHost Build(string[] args, Action<HostApplicationBuilder>? configure = null)
    {
        var builder = Host.CreateApplicationBuilder(args);
        configure?.Invoke(builder);
        LocalHosting.RequireLocal(builder.Environment);
        Telemetry.Configure(builder.Services, builder.Configuration, "relaylab-worker");
        builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.None);
        var settings = WorkerSettings.From(builder.Configuration);
        builder.Services.AddSingleton(settings);
        builder.Services.AddDbContextFactory<RelayDb>(o => o.UseSqlServer(LocalHosting.Connection(builder.Configuration, "RelayLab")));
        builder.Services.AddSingleton(_ => WorkerSettings.CreateBus(LocalHosting.Connection(builder.Configuration, "ServiceBus")));
        builder.Services.AddSingleton(sp => sp.GetRequiredService<ServiceBusClient>().CreateSender(settings.Queue));
        builder.Services.AddSingleton(_ => WorkerSettings.CreateHttpClient());
        builder.Services.AddSingleton<OutboxPublisher>();
        builder.Services.AddSingleton<DeliveryProcessor>();
        builder.Services.AddSingleton<DeliveryTransitions>();
        builder.Services.TryAddSingleton<WorkerBoundary>();
        builder.Services.AddHostedService<PublishingService>();
        builder.Services.AddHostedService<ConsumingService>();
        return builder.Build();
    }
}
