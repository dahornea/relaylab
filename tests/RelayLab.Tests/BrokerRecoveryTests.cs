using Azure.Messaging.ServiceBus;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RelayLab.Worker;
using Xunit;

namespace RelayLab.Tests;

[Collection("Infrastructure")]
[Trait("Category", "Integration")]
public sealed class BrokerRecoveryTests(Infrastructure infrastructure)
{
    [Fact]
    public async Task Broker_delivery_exhaustion_is_preserved_and_SQL_reconciliation_recovers_current_work()
    {
        await using var runtime = new TestRuntime(infrastructure);
        await runtime.StartAsync();
        using var accepted = await runtime.SubmitAsync("broker-exhaustion");
        var id = await TestRuntime.IdAsync(accepted);
        var host = await runtime.StartWorkerAsync(start: false);
        Assert.True(await host.Services.GetRequiredService<OutboxPublisher>().PublishOneAsync(default));
        await using var bus = WorkerSettings.CreateBus(infrastructure.BusConnection);
        await using var receiver = bus.CreateReceiver("deliveries");
        string? workId = null;
        for (var i = 0; i < 10; i++)
        {
            var message = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
            Assert.NotNull(message);
            workId ??= message.MessageId;
            Assert.Equal(workId, message.MessageId);
            await receiver.AbandonMessageAsync(message);
        }
        await using var deadLetters = bus.CreateReceiver("deliveries", new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter });
        ServiceBusReceivedMessage? dead = null;
        await TestRuntime.EventuallyAsync(async () =>
        {
            // A receive causes the broker to enforce delivery exhaustion if enforcement is lazy.
            var extra = await receiver.ReceiveMessageAsync(TimeSpan.FromMilliseconds(200));
            if (extra is not null) await receiver.AbandonMessageAsync(extra);
            dead = await deadLetters.PeekMessageAsync(fromSequenceNumber: 0);
            return dead is not null;
        }, "Valid work did not enter the real broker dead-letter queue.");
        Assert.Equal("MaxDeliveryCountExceeded", dead!.DeadLetterReason);
        Assert.Equal(workId, dead.MessageId);
        await using (var db = runtime.OpenRelay())
        {
            Assert.Equal("Pending", (await db.Deliveries.SingleAsync()).Status);
            Assert.Empty(await db.DeliveryAttempts.ToListAsync());
        }
        await RetryTests.MakeEligibleAsync(runtime, id);
        await runtime.StartWorkerAsync();
        await runtime.WaitForAsync(id, "Delivered");
        Assert.Equal(workId, (await deadLetters.PeekMessageAsync(fromSequenceNumber: 0)).MessageId);
        await using var final = runtime.OpenRelay();
        Assert.Single(await final.DeliveryAttempts.ToListAsync());
        Assert.True((await final.OutboxMessages.SingleAsync()).SendCount >= 2);
        await using var recipient = runtime.OpenReceiver();
        Assert.Single(await recipient.Effects.ToListAsync());
    }

    [Fact]
    public async Task Broker_connectivity_outage_preserves_sentinel_and_new_SQL_intent_then_recovers()
    {
        await using var runtime = new TestRuntime(infrastructure, retryBaseSeconds: 1);
        await runtime.StartAsync();
        await using var gate = new BrokerGate(infrastructure.BusConnection);
        await using var client = WorkerSettings.CreateBus(gate.Connection);
        await using var sender = client.CreateSender("deliveries");
        var sentinel = "sentinel-" + Guid.NewGuid().ToString("N");
        await sender.SendMessageAsync(new ServiceBusMessage("synthetic-connectivity-sentinel") { MessageId = sentinel });
        gate.Pause();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        await Assert.ThrowsAnyAsync<Exception>(() => sender.SendMessageAsync(new ServiceBusMessage("blocked-probe"), deadline.Token));
        using var accepted = await runtime.SubmitAsync("during-broker-outage");
        var id = await TestRuntime.IdAsync(accepted);
        await runtime.StartWorkerAsync(busConnection: gate.Connection);
        await TestRuntime.EventuallyAsync(async () =>
        {
            await using var db = runtime.OpenRelay();
            return await db.OutboxMessages.AnyAsync(o => o.LastError == "BrokerUnavailable");
        }, "The worker did not observe the real broker outage.");
        await runtime.StopWorkerAsync();
        await using (var db = runtime.OpenRelay())
        {
            Assert.Equal("Pending", (await db.Deliveries.SingleAsync()).Status);
            Assert.Empty(await db.DeliveryAttempts.ToListAsync());
            Assert.Null((await db.OutboxMessages.SingleAsync()).PublishedUtc);
        }
        gate.Resume();
        await using var restored = WorkerSettings.CreateBus(gate.Connection);
        await using var receiver = restored.CreateReceiver("deliveries");
        var preserved = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(15));
        Assert.NotNull(preserved);
        Assert.Equal(sentinel, preserved.MessageId);
        await receiver.CompleteMessageAsync(preserved);
        await RetryTests.MakeEligibleAsync(runtime, id);
        await runtime.StartWorkerAsync(busConnection: gate.Connection);
        await runtime.WaitForAsync(id, "Delivered");
        await runtime.StopWorkerAsync();
        await using var recipient = runtime.OpenReceiver();
        Assert.Single(await recipient.Effects.ToListAsync());
    }
}
