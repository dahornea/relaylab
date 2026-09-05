using System.Net;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace RelayLab.Tests;

[Collection("Infrastructure")]
[Trait("Category", "Integration")]
public sealed class TelemetryTests(Infrastructure infrastructure)
{
    [Fact]
    public async Task Unavailable_OTLP_viewer_does_not_block_durable_delivery_or_status()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        using (var probe = new TcpClient())
            await Assert.ThrowsAsync<SocketException>(async () => await probe.ConnectAsync(IPAddress.Loopback, port));
        await using var runtime = new TestRuntime(infrastructure, telemetryEndpoint: $"http://127.0.0.1:{port}");
        await runtime.StartAsync();
        using var accepted = await runtime.SubmitAsync("viewer-offline");
        var id = await TestRuntime.IdAsync(accepted);
        await runtime.StartWorkerAsync();
        await runtime.WaitForAsync(id, "Delivered");
        using var status = await runtime.Api.GetAsync($"/deliveries/{id}");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        await using var db = runtime.OpenRelay();
        Assert.Equal("Acknowledged", (await db.DeliveryAttempts.SingleAsync()).Outcome);
        Assert.NotNull((await db.Deliveries.SingleAsync()).TraceParent);
        await using var recipient = runtime.OpenReceiver();
        Assert.Single(await recipient.Effects.ToListAsync());
    }
}
