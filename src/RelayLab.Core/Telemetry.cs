using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Azure.Monitor.OpenTelemetry.Exporter;

namespace RelayLab.Core;

public static class Telemetry
{
    public const string Name = "RelayLab";
    public static readonly ActivitySource Activities = new(Name);
    public static readonly Meter Meter = new(Name);
    public static readonly Counter<long> Accepted = Meter.CreateCounter<long>("relaylab.accepted");
    public static readonly Counter<long> Publications = Meter.CreateCounter<long>("relaylab.publications");
    public static readonly Counter<long> Outcomes = Meter.CreateCounter<long>("relaylab.attempt.outcomes");
    public static readonly Counter<long> Attempts = Meter.CreateCounter<long>("relaylab.attempt.started");
    public static readonly Counter<long> Replays = Meter.CreateCounter<long>("relaylab.replays");
    public static readonly Counter<long> Effects = Meter.CreateCounter<long>("relaylab.receiver.effects");
    public static readonly Histogram<double> HttpDuration = Meter.CreateHistogram<double>("relaylab.http.duration", "s");
    private static long pending;
    private static readonly ObservableGauge<long> Pending = Meter.CreateObservableGauge("relaylab.pending", () => Interlocked.Read(ref pending));
    public static void ObservePending(long count) => Interlocked.Exchange(ref pending, count);

    public static void Configure(IServiceCollection services, IConfiguration configuration, string service)
    {
        var enabled = !string.IsNullOrWhiteSpace(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);
        var azure = !string.IsNullOrWhiteSpace(configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]);
        var instance = Guid.NewGuid().ToString("N");
        services.AddOpenTelemetry().ConfigureResource(r => r.AddService(service, serviceInstanceId: instance))
            .WithTracing(t =>
            {
                t.AddSource(Name).SetSampler(new AlwaysOnSampler());
                if (enabled) t.AddOtlpExporter(o => o.Endpoint = new Uri(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]!));
                if (azure) t.AddAzureMonitorTraceExporter(o => { o.ConnectionString = configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]; o.Credential = CloudHosting.Credential(configuration); });
            })
            .WithMetrics(m =>
            {
                m.AddMeter(Name);
                m.AddView("relaylab.http.duration", new ExplicitBucketHistogramConfiguration
                {
                    Boundaries = [0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2, 5, 10, 20]
                });
                if (enabled) m.AddOtlpExporter(o => o.Endpoint = new Uri(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]!));
                if (azure) m.AddAzureMonitorMetricExporter(o => { o.ConnectionString = configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]; o.Credential = CloudHosting.Credential(configuration); });
            });
    }

    public static Activity? Start(string name, Delivery delivery, ActivityKind kind = ActivityKind.Internal)
    {
        ActivityContext.TryParse(delivery.TraceParent, null, out var parent);
        var activity = Activities.StartActivity(name, kind, parent);
        activity?.SetTag("relaylab.delivery.id", delivery.Id.ToString("D"));
        activity?.SetTag("relaylab.work.id", delivery.WorkId.ToString("D"));
        activity?.SetTag("relaylab.generation", delivery.Generation);
        activity?.SetTag("relaylab.attempt.number", delivery.AttemptNumber);
        return activity;
    }
}
