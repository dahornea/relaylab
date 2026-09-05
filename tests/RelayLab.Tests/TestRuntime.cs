using System.Net.Http.Json;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RelayLab.Core;
using RelayLab.Receiver;
using Xunit;

namespace RelayLab.Tests;

public sealed class TestRuntime(Infrastructure infrastructure, IInterceptor? acceptanceObserver = null) : IAsyncDisposable
{
    private readonly string databaseName = "RelayLabTest_" + Guid.NewGuid().ToString("N");
    private WebApplication? api;
    private WebApplication? receiver;
    private IHost? worker;
    public HttpClient Api { get; private set; } = null!;
    public HttpClient Receiver { get; private set; } = null!;
    public string RelayConnection => new SqlConnectionStringBuilder(infrastructure.SqlConnection) { InitialCatalog = databaseName }.ConnectionString;
    public string ReceiverConnection => new SqlConnectionStringBuilder(infrastructure.SqlConnection) { InitialCatalog = databaseName + "_Receiver" }.ConnectionString;
    public RelayDb OpenRelay() => new(new DbContextOptionsBuilder<RelayDb>().UseSqlServer(RelayConnection).Options);
    public ReceiverDb OpenReceiver() => new(new DbContextOptionsBuilder<ReceiverDb>().UseSqlServer(ReceiverConnection).Options);

    public async Task StartAsync()
    {
        await infrastructure.DrainAsync();
        await using (var db = OpenRelay()) await db.Database.EnsureCreatedAsync();
        await using (var db = OpenReceiver()) await db.Database.EnsureCreatedAsync();
        await StartReceiverAsync();
        await StartApiAsync();
    }

    private void Configure(WebApplicationBuilder builder)
    {
        builder.Environment.EnvironmentName = "Testing";
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:RelayLab"] = RelayConnection,
            ["ConnectionStrings:Receiver"] = ReceiverConnection
        });
    }

    public async Task StartApiAsync()
    {
        api = RelayLab.Api.Program.Build([], builder =>
        {
            Configure(builder);
            if (acceptanceObserver is not null)
                builder.Services.AddDbContextFactory<RelayDb>(options => options.AddInterceptors(acceptanceObserver));
        });
        await api.StartAsync();
        Api = new HttpClient { BaseAddress = new Uri(api.Urls.Single()), Timeout = TimeSpan.FromSeconds(30) };
    }

    public async Task RestartApiAsync()
    {
        Api.Dispose();
        await api!.StopAsync();
        await api.DisposeAsync();
        await StartApiAsync();
    }

    public async Task StartReceiverAsync()
    {
        receiver = RelayLab.Receiver.Program.Build([], Configure);
        await receiver.StartAsync();
        Receiver = new HttpClient { BaseAddress = new Uri(receiver.Urls.Single()), Timeout = TimeSpan.FromSeconds(30) };
    }

    public async Task RestartReceiverAsync()
    {
        Receiver.Dispose();
        await receiver!.StopAsync();
        await receiver.DisposeAsync();
        await StartReceiverAsync();
    }

    public async Task StartWorkerAsync(Uri? destination = null, int timeoutSeconds = 10)
    {
        worker = RelayLab.Worker.Program.Build([], builder =>
        {
            builder.Environment.EnvironmentName = "Testing";
            builder.Logging.ClearProviders();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:RelayLab"] = RelayConnection,
                ["ConnectionStrings:ServiceBus"] = infrastructure.BusConnection,
                ["RelayLab:DestinationUrl"] = (destination ?? new Uri(Receiver.BaseAddress!, "/webhooks")).AbsoluteUri,
                ["RelayLab:HttpTimeoutSeconds"] = timeoutSeconds.ToString(),
                ["RelayLab:Queue"] = "deliveries"
            });
        });
        await worker.StartAsync();
    }

    public async Task StopWorkerAsync()
    {
        if (worker is null) return;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await worker.StopAsync(deadline.Token);
        if (worker is IAsyncDisposable disposable) await disposable.DisposeAsync();
        else worker.Dispose();
        worker = null;
    }

    public Task<HttpResponseMessage> SubmitAsync(string key, string document = "doc-001")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/events") { Content = JsonContent.Create(Event(document)) };
        request.Headers.Add("Idempotency-Key", key);
        return SendAndDisposeAsync(Api, request);
    }

    public Task<HttpResponseMessage> DeliverAsync(Guid id, string document = "doc-001")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks") { Content = JsonContent.Create(Event(document)) };
        request.Headers.Add("X-RelayLab-Delivery-Id", id.ToString());
        return SendAndDisposeAsync(Receiver, request);
    }

    private static async Task<HttpResponseMessage> SendAndDisposeAsync(HttpClient client, HttpRequestMessage request)
    {
        using (request) return await client.SendAsync(request);
    }

    public static EventRequest Event(string document = "doc-001") => new("demo", "document.ready", new(document));
    public static async Task<Guid> IdAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("deliveryId").GetGuid();
    }

    public async Task<Delivery> WaitForAsync(Guid id, string status)
    {
        Delivery? observed = null;
        await EventuallyAsync(async () =>
        {
            await using var db = OpenRelay();
            observed = await db.Deliveries.AsNoTracking().SingleAsync(d => d.Id == id);
            return observed.Status == status;
        }, $"Delivery did not become {status}.");
        return observed!;
    }

    public static async Task EventuallyAsync(Func<Task<bool>> condition, string failure, int seconds = 45)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        while (!deadline.IsCancellationRequested)
        {
            if (await condition()) return;
            try { await Task.Delay(100, deadline.Token); }
            catch (OperationCanceledException) { break; }
        }
        Assert.Fail(failure);
    }

    public async Task RepublishAsync(Guid id)
    {
        await using var db = OpenRelay();
        var delivery = await db.Deliveries.AsNoTracking().SingleAsync(d => d.Id == id);
        await using var bus = RelayLab.Worker.WorkerSettings.CreateBus(infrastructure.BusConnection);
        await using var sender = bus.CreateSender("deliveries");
        await sender.SendMessageAsync(new ServiceBusMessage(JsonSerializer.Serialize(new WorkEnvelope(1, id, delivery.WorkId), EventContract.Json))
        { MessageId = delivery.WorkId.ToString("D") });
    }

    public async ValueTask DisposeAsync()
    {
        await StopWorkerAsync();
        Api?.Dispose();
        Receiver?.Dispose();
        if (api is not null) { await api.StopAsync(); await api.DisposeAsync(); }
        if (receiver is not null) { await receiver.StopAsync(); await receiver.DisposeAsync(); }
        await using (var db = OpenRelay()) await db.Database.EnsureDeletedAsync();
        await using (var db = OpenReceiver()) await db.Database.EnsureDeletedAsync();
    }
}
