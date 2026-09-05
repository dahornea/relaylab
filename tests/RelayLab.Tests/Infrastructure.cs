using System.Text.Json;
using Azure.Messaging.ServiceBus;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Networks;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;
using Testcontainers.ServiceBus;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace RelayLab.Tests;

public sealed class Infrastructure : IAsyncLifetime
{
    private readonly INetwork network = new NetworkBuilder().Build();
    private readonly MsSqlContainer sql;
    private readonly ServiceBusContainer broker;

    public Infrastructure()
    {
        using var versions = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "infra/versions.json")));
        var password = $"RL!{Guid.NewGuid():N}a9";
        sql = new MsSqlBuilder(versions.RootElement.GetProperty("sql").GetString()!)
            .WithPassword(password).WithNetwork(network).WithNetworkAliases("sql")
            .Build();
        broker = new ServiceBusBuilder(versions.RootElement.GetProperty("serviceBus").GetString()!)
            .WithAcceptLicenseAgreement(true)
            .WithMsSqlContainer(network, sql, "sql", password)
            .WithConfig(Path.Combine(AppContext.BaseDirectory, "infra/ServiceBusConfig.json"))
            .Build();
    }

    public string SqlConnection => sql.GetConnectionString();
    public string BusConnection => broker.GetConnectionString();

    public async Task WaitUntilQueueEmptyAsync()
    {
        await using var client = new ServiceBusClient(BusConnection);
        await using var receiver = client.CreateReceiver("deliveries");
        await TestRuntime.EventuallyAsync(async () => (await receiver.PeekMessagesAsync(1, fromSequenceNumber: 0)).Count == 0,
            "Expected durable outcomes to be followed by broker settlement.");
    }

    public async Task DrainAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var client = new ServiceBusClient(BusConnection);
        foreach (var subQueue in new[] { SubQueue.None, SubQueue.DeadLetter })
        {
            await using var receiver = client.CreateReceiver("deliveries", new ServiceBusReceiverOptions { SubQueue = subQueue });
            while (await receiver.ReceiveMessageAsync(TimeSpan.FromMilliseconds(200), deadline.Token) is { } message)
                await receiver.CompleteMessageAsync(message, deadline.Token);
        }
    }

    public async Task InitializeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        try
        {
            await network.CreateAsync(timeout.Token);
            await sql.StartAsync(timeout.Token);
            await broker.StartAsync(timeout.Token);
        }
        catch
        {
            await SaveLogsAsync();
            await DisposeAsync();
            throw;
        }
    }

    public async Task SaveLogsAsync()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "TestResults", "containers");
        Directory.CreateDirectory(directory);
        foreach (var (container, name) in new[] { ((DotNet.Testcontainers.Containers.IContainer)sql, "sql"), (broker, "servicebus") })
        {
            try
            {
                var (stdout, stderr) = await container.GetLogsAsync();
                await File.WriteAllTextAsync(Path.Combine(directory, name + ".log"), stdout + stderr);
            }
            catch (Exception) { /* Startup can fail before a container exists. */ }
        }
    }

    public async Task DisposeAsync()
    {
        await SaveLogsAsync();
        await broker.DisposeAsync();
        await sql.DisposeAsync();
        await network.DisposeAsync();
    }
}

[CollectionDefinition("Infrastructure")]
public sealed class InfrastructureCollection : ICollectionFixture<Infrastructure>;

[Collection("Infrastructure")]
public sealed class ConnectivityTests(Infrastructure infrastructure)
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Sql_and_broker_send_receive_are_real()
    {
        await infrastructure.DrainAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var connection = new SqlConnection(infrastructure.SqlConnection);
        await connection.OpenAsync(deadline.Token);
        await using var command = new SqlCommand("SELECT 1", connection);
        Assert.Equal(1, await command.ExecuteScalarAsync(deadline.Token));
        await using var client = new ServiceBusClient(infrastructure.BusConnection);
        await using var sender = client.CreateSender("deliveries");
        await using var receiver = client.CreateReceiver("deliveries", new ServiceBusReceiverOptions { ReceiveMode = ServiceBusReceiveMode.PeekLock });
        var id = Guid.NewGuid().ToString();
        await sender.SendMessageAsync(new ServiceBusMessage("connectivity") { MessageId = id }, deadline.Token);
        var message = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(30), deadline.Token);
        Assert.NotNull(message);
        Assert.Equal(id, message.MessageId);
        await receiver.CompleteMessageAsync(message, deadline.Token);
    }
}
