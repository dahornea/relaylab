using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RelayLab.Core;
using RelayLab.Receiver;
using RelayLab.Worker;

namespace RelayLab.Tests;

internal sealed class BoundaryGate(string point) : IDisposable
{
    private readonly TaskCompletionSource<Guid> arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public WorkerBoundary Observer => new() { Reached = HitAsync };
    public Task<Guid> ArrivedAsync() => arrived.Task.WaitAsync(TimeSpan.FromSeconds(30));
    public void Release() => released.TrySetResult();
    public void Dispose() => Release();
    private async Task HitAsync(string name, Guid workId, CancellationToken ct)
    {
        if (name == point && arrived.TrySetResult(workId)) await released.Task.WaitAsync(ct);
    }
}

internal sealed class FaultReceiver : IAsyncDisposable
{
    private readonly WebApplication app;
    public Uri Address => new(app.Urls.Single() + "/webhooks");
    public int Status { get; set; } = 503;
    public bool CommitThenAbort { get; set; }
    private FaultReceiver(WebApplication app) => this.app = app;
    public static async Task<FaultReceiver> StartAsync(TestRuntime runtime)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddDbContextFactory<ReceiverDb>(o => o.UseSqlServer(runtime.ReceiverConnection));
        builder.Services.AddTransient<ReceiverLedger>();
        var app = builder.Build();
        var receiver = new FaultReceiver(app);
        app.MapPost("/webhooks", async (HttpContext context, ReceiverLedger ledger) =>
        {
            if (receiver.CommitThenAbort || receiver.Status == 200)
            {
                var id = Guid.Parse(context.Request.Headers["X-RelayLab-Delivery-Id"].ToString());
                var request = await context.Request.ReadFromJsonAsync<EventRequest>(EventContract.Json, context.RequestAborted);
                if (!await ledger.RecordAsync(id, request!, context.RequestAborted)) throw new InvalidOperationException("Conflicting test effect.");
            }
            if (receiver.CommitThenAbort) context.Abort();
            else context.Response.StatusCode = receiver.Status;
        });
        await app.StartAsync();
        return receiver;
    }
    public async ValueTask DisposeAsync() { await app.StopAsync(); await app.DisposeAsync(); }
}
