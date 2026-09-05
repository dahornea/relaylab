using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Configuration;

namespace RelayLab.Worker;

public sealed record WorkerSettings(Uri Destination, string Queue, int HttpTimeoutSeconds = 10,
    int LeaseSeconds = 30, int MaxConcurrentCalls = 2, int PublishRetrySeconds = 3, int ReconcileSeconds = 30)
{
    public static WorkerSettings From(IConfiguration configuration)
    {
        var settings = new WorkerSettings(
            new Uri(configuration["RelayLab:DestinationUrl"] ?? throw new InvalidOperationException("Configure RelayLab:DestinationUrl.")),
            configuration["RelayLab:Queue"] ?? "deliveries",
            configuration.GetValue("RelayLab:HttpTimeoutSeconds", 10),
            configuration.GetValue("RelayLab:LeaseSeconds", 30),
            configuration.GetValue("RelayLab:MaxConcurrentCalls", 2),
            configuration.GetValue("RelayLab:PublishRetrySeconds", 3),
            configuration.GetValue("RelayLab:ReconcileSeconds", 30));
        if (settings.Destination.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(settings.Destination.UserInfo) ||
            !string.IsNullOrEmpty(settings.Destination.Fragment) || settings.HttpTimeoutSeconds is < 1 or > 20 ||
            settings.LeaseSeconds < settings.HttpTimeoutSeconds + 10 || settings.LeaseSeconds > 45 ||
            settings.MaxConcurrentCalls is < 1 or > 4 || settings.PublishRetrySeconds is < 1 or > 30 ||
            settings.ReconcileSeconds is < 5 or > 300 || string.IsNullOrWhiteSpace(settings.Queue))
            throw new InvalidOperationException("Invalid RelayLab destination or processing bounds.");
        return settings;
    }

    public static ServiceBusClient CreateBus(string connection) => new(connection, new ServiceBusClientOptions
    {
        TransportType = ServiceBusTransportType.AmqpTcp,
        RetryOptions = new ServiceBusRetryOptions { MaxRetries = 0, TryTimeout = TimeSpan.FromSeconds(5) }
    });

    public static ServiceBusClient CreateCloudBus(IConfiguration configuration)
    {
        var name = configuration["Azure:ServiceBusNamespace"] ?? "";
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^[a-zA-Z0-9-]+\.servicebus\.windows\.net$"))
            throw new InvalidOperationException("Configure the Azure Service Bus namespace hostname.");
        return new(name, RelayLab.Core.CloudHosting.Credential(configuration), new ServiceBusClientOptions
        { TransportType = ServiceBusTransportType.AmqpTcp, RetryOptions = new() { MaxRetries = 0, TryTimeout = TimeSpan.FromSeconds(5) } });
    }

    public static HttpClient CreateHttpClient(DelegatingHandler? authorization = null)
    {
        var transport = new SocketsHttpHandler
        {
        AllowAutoRedirect = false,
        UseCookies = false,
        MaxResponseHeadersLength = 16,
        MaxResponseDrainSize = 0,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        };
        if (authorization is null) return new(transport) { Timeout = Timeout.InfiniteTimeSpan };
        authorization.InnerHandler = transport;
        return new(authorization) { Timeout = Timeout.InfiniteTimeSpan };
    }
}
