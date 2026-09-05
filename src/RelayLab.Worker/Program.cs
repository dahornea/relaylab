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
        var environment = host.Services.GetRequiredService<IHostEnvironment>();
        if (CloudHosting.IsCloud(environment))
        {
            await using var db = await host.Services.GetRequiredService<IDbContextFactory<RelayDb>>().CreateDbContextAsync();
            await SchemaDeployment.VerifyAsync(db, "relay", default);
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
        var cloud = CloudHosting.IsCloud(builder.Environment);
        if (cloud && (settings.Destination.Scheme != "https" || settings.Destination.Port != 443))
            throw new InvalidOperationException("Production receivers require HTTPS on port 443.");
        builder.Services.AddDbContextFactory<RelayDb>(o => o.UseSqlServer(CloudHosting.SqlConnection(builder.Configuration, "RelayLab", cloud)));
        builder.Services.AddSingleton(_ => cloud ? WorkerSettings.CreateCloudBus(builder.Configuration)
            : WorkerSettings.CreateBus(LocalHosting.Connection(builder.Configuration, "ServiceBus")));
        builder.Services.AddSingleton(sp => sp.GetRequiredService<ServiceBusClient>().CreateSender(settings.Queue));
        builder.Services.AddSingleton(_ => WorkerSettings.CreateHttpClient(cloud ? new ReceiverAuthorizationHandler(
            CloudHosting.Credential(builder.Configuration), CloudHosting.RequiredGuid(builder.Configuration, "Security:ReceiverAudience"), settings.Destination) : null));
        builder.Services.AddSingleton<OutboxPublisher>();
        builder.Services.AddSingleton<DeliveryProcessor>();
        builder.Services.AddSingleton<DeliveryTransitions>();
        builder.Services.TryAddSingleton<WorkerBoundary>();
        builder.Services.AddHostedService<PublishingService>();
        builder.Services.AddHostedService<ConsumingService>();
        return builder.Build();
    }
}
