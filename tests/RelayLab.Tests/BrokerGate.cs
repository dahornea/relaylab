using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Azure.Messaging.ServiceBus;

namespace RelayLab.Tests;

// Test-owned TCP interruption, preserving the emulator process and its stored messages.
internal sealed class BrokerGate : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stopping = new();
    private readonly ConcurrentDictionary<TcpClient, byte> connections = new();
    private readonly ConcurrentBag<Task> pumps = [];
    private readonly Uri target;
    private readonly Task accepting;
    private volatile bool blocked;
    public string Connection { get; }

    public BrokerGate(string connection)
    {
        target = ServiceBusConnectionStringProperties.Parse(connection).Endpoint;
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Connection = Regex.Replace(connection, @"Endpoint=[^;]+", $"Endpoint=sb://127.0.0.1:{port}/", RegexOptions.IgnoreCase);
        accepting = AcceptAsync();
    }

    public void Pause()
    {
        blocked = true;
        foreach (var client in connections.Keys) client.Dispose();
    }
    public void Resume() => blocked = false;

    private async Task AcceptAsync()
    {
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                var incoming = await listener.AcceptTcpClientAsync(stopping.Token);
                if (blocked) incoming.Dispose();
                else pumps.Add(ForwardAsync(incoming));
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
    }

    private async Task ForwardAsync(TcpClient incoming)
    {
        using (incoming)
        using (var outgoing = new TcpClient())
        using (var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token))
        {
            connections.TryAdd(incoming, 0);
            connections.TryAdd(outgoing, 0);
            try
            {
                if (blocked) return;
                await outgoing.ConnectAsync(target.Host, target.Port, lifetime.Token);
                if (blocked) return;
                var send = incoming.GetStream().CopyToAsync(outgoing.GetStream(), lifetime.Token);
                var receive = outgoing.GetStream().CopyToAsync(incoming.GetStream(), lifetime.Token);
                await Task.WhenAny(send, receive);
                await lifetime.CancelAsync();
                incoming.Dispose();
                outgoing.Dispose();
                await Task.WhenAll(send, receive);
            }
            catch (Exception error) when (error is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
            finally { connections.TryRemove(incoming, out _); connections.TryRemove(outgoing, out _); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await stopping.CancelAsync();
        Pause();
        listener.Stop();
        await accepting;
        await Task.WhenAll(pumps);
        stopping.Dispose();
    }
}
