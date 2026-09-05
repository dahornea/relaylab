using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Configuration;

namespace RelayLab.Worker;

public sealed record WorkerSettings(Uri Destination, string Queue, int HttpTimeoutSeconds = 10,
    int LeaseSeconds = 30, int MaxConcurrentCalls = 2, int PublishRetrySeconds = 3)
{
    public static WorkerSettings From(IConfiguration configuration)
    {
        var settings = new WorkerSettings(
            new Uri(configuration["RelayLab:DestinationUrl"] ?? throw new InvalidOperationException("Configure RelayLab:DestinationUrl.")),
            configuration["RelayLab:Queue"] ?? "deliveries",
            configuration.GetValue("RelayLab:HttpTimeoutSeconds", 10),
            configuration.GetValue("RelayLab:LeaseSeconds", 30),
            configuration.GetValue("RelayLab:MaxConcurrentCalls", 2),
            configuration.GetValue("RelayLab:PublishRetrySeconds", 3));
        if (settings.Destination.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(settings.Destination.UserInfo) ||
            !string.IsNullOrEmpty(settings.Destination.Fragment) || settings.HttpTimeoutSeconds is < 1 or > 20 ||
            settings.LeaseSeconds < settings.HttpTimeoutSeconds + 10 || settings.LeaseSeconds > 45 ||
            settings.MaxConcurrentCalls is < 1 or > 4 || settings.PublishRetrySeconds is < 1 or > 30 || string.IsNullOrWhiteSpace(settings.Queue))
            throw new InvalidOperationException("Invalid RelayLab destination or processing bounds.");
        return settings;
    }

    public static ServiceBusClient CreateBus(string connection) => new(connection, new ServiceBusClientOptions
    {
        TransportType = ServiceBusTransportType.AmqpTcp,
        RetryOptions = new ServiceBusRetryOptions { MaxRetries = 0, TryTimeout = TimeSpan.FromSeconds(5) }
    });

    public static HttpClient CreateHttpClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        MaxResponseHeadersLength = 16,
        MaxResponseDrainSize = 0,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2)
    }) { Timeout = Timeout.InfiniteTimeSpan };
}
