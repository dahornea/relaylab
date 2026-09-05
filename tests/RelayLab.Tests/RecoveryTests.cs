using Azure.Messaging.ServiceBus;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RelayLab.Core;
using RelayLab.Worker;
using Xunit;

namespace RelayLab.Tests;

[Collection("Infrastructure")]
[Trait("Category", "Integration")]
public sealed class RecoveryTests(Infrastructure infrastructure)
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Publisher_interruption_after_send_reuses_work_identity_and_fences_stale_publication_owner(bool interrupt)
    {
        await using var runtime = new TestRuntime(infrastructure);
        await runtime.StartAsync();
        using var accepted = await runtime.SubmitAsync("publish-crash");
        var id = await TestRuntime.IdAsync(accepted);
        using var gate = new BoundaryGate("AfterPublish");
        var hostA = await runtime.StartWorkerAsync(boundary: gate.Observer, start: false);
        using var cancellation = new CancellationTokenSource();
        var sending = hostA.Services.GetRequiredService<OutboxPublisher>().PublishOneAsync(cancellation.Token);
        var workId = await gate.ArrivedAsync();
        await using var bus = WorkerSettings.CreateBus(infrastructure.BusConnection);
        await using var receiver = bus.CreateReceiver("deliveries");
        var first = await receiver.PeekMessageAsync();
        Assert.Equal(workId.ToString("D"), first.MessageId);
        if (interrupt)
        {
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
        }
        await using (var db = runtime.OpenRelay())
        {
            Assert.Null((await db.OutboxMessages.SingleAsync()).PublishedUtc);
            await db.Database.ExecuteSqlRawAsync("UPDATE OutboxMessages SET LeaseUntilUtc=DATEADD(second,-1,SYSUTCDATETIME())");
        }
        var hostB = await runtime.StartWorkerAsync(start: false);
        Assert.True(await hostB.Services.GetRequiredService<OutboxPublisher>().PublishOneAsync(default));
        DateTime published;
        await using (var db = runtime.OpenRelay()) published = (await db.OutboxMessages.SingleAsync()).PublishedUtc!.Value;
        if (!interrupt)
        {
            gate.Release(); // expired A attempts its fenced publication mark after B's commit
            Assert.True(await sending);
        }
        await using (var db = runtime.OpenRelay())
        {
            var outbox = await db.OutboxMessages.SingleAsync();
            Assert.Equal(2, outbox.SendCount);
            Assert.Equal(published, outbox.PublishedUtc);
        }
        var signals = await receiver.PeekMessagesAsync(10, fromSequenceNumber: 0);
        Assert.Equal(2, signals.Count);
        Assert.All(signals, m => Assert.Equal(workId.ToString("D"), m.MessageId));
        await runtime.StartWorkerAsync();
        await runtime.WaitForAsync(id, "Delivered");
        await infrastructure.WaitUntilQueueEmptyAsync();
        await using var recipient = runtime.OpenReceiver();
        Assert.Single(await recipient.Effects.ToListAsync());
    }

    [Theory]
    [InlineData("AfterClaim", 2)]
    [InlineData("AfterHttp", 2)]
    [InlineData("AfterTransition", 1)]
    public async Task Interruption_at_durable_boundary_recovers_without_losing_intent(string boundary, int expectedAttempts)
    {
        await using var runtime = new TestRuntime(infrastructure, retryBaseSeconds: 1);
        await runtime.StartAsync();
        using var gate = new BoundaryGate(boundary);
        using var accepted = await runtime.SubmitAsync("boundary-" + boundary);
        var id = await TestRuntime.IdAsync(accepted);
        await runtime.StartWorkerAsync(boundary: gate.Observer);
        var workId = await gate.ArrivedAsync();
        await runtime.StopWorkerAsync(); // cancellation while held; completion/settlement cannot cross the barrier
        await using (var db = runtime.OpenRelay())
        {
            var attempt = Assert.Single(await db.DeliveryAttempts.ToListAsync());
            Assert.Equal(boundary == "AfterTransition" ? "Acknowledged" : "Started", attempt.Outcome);
            if (boundary != "AfterTransition") Assert.Null(attempt.CompletedUtc);
            await db.Database.ExecuteSqlRawAsync("UPDATE Deliveries SET LeaseUntilUtc=DATEADD(second,-1,SYSUTCDATETIME()) WHERE Status='Processing'");
        }
        await runtime.RestartReceiverAsync();
        await runtime.StartWorkerAsync();
        await runtime.RepublishAsync(id, workId);
        await runtime.WaitForAsync(id, "Delivered");
        await infrastructure.WaitUntilQueueEmptyAsync();
        await using var final = runtime.OpenRelay();
        var attempts = await final.DeliveryAttempts.OrderBy(a => a.Sequence).ToListAsync();
        Assert.Equal(expectedAttempts, attempts.Count);
        if (expectedAttempts == 2)
        {
            Assert.Equal("Interrupted", attempts[0].Outcome);
            Assert.Equal("Unknown", attempts[0].RemoteOutcome);
            Assert.Null(attempts[0].CompletedUtc);
            Assert.NotEqual(attempts[0].WorkId, attempts[1].WorkId);
        }
        await using var recipient = runtime.OpenReceiver();
        Assert.Single(await recipient.Effects.ToListAsync());
    }

    [Fact]
    public async Task Competing_workers_and_duplicate_signals_cannot_let_expired_owner_overwrite_recovery()
    {
        await using var runtime = new TestRuntime(infrastructure, retryBaseSeconds: 1);
        await runtime.StartAsync();
        var arrived = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stale = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observer = new WorkerBoundary { Reached = async (point, work, ct) =>
        {
            if (point == "AfterHttp" && arrived.TrySetResult(work)) await release.Task.WaitAsync(ct);
            if (point == "StaleCompletion") stale.TrySetResult();
        }};
        using var accepted = await runtime.SubmitAsync("competing");
        var id = await TestRuntime.IdAsync(accepted);
        var ownerA = await runtime.StartWorkerAsync(boundary: observer, concurrency: 1);
        var oldWork = await arrived.Task.WaitAsync(TimeSpan.FromSeconds(30));
        try
        {
            var duplicatesHandled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var handled = 0;
            var ownerB = await runtime.StartWorkerAsync(boundary: new WorkerBoundary { Reached = (point, work, _) =>
            {
                if (point == "BeforeComplete" && work == oldWork && Interlocked.Increment(ref handled) >= 8) duplicatesHandled.TrySetResult();
                return Task.CompletedTask;
            }});
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => runtime.RepublishAsync(id, oldWork)));
            await duplicatesHandled.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await using (var db = runtime.OpenRelay())
            {
                Assert.Single(await db.DeliveryAttempts.ToListAsync());
                await db.Database.ExecuteSqlRawAsync("UPDATE Deliveries SET LeaseUntilUtc=DATEADD(second,-1,SYSUTCDATETIME()) WHERE Status='Processing'");
            }
            var delivered = await runtime.WaitForAsync(id, "Delivered");
            Assert.NotEqual(oldWork, delivered.WorkId);
            release.TrySetResult();
            await stale.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await runtime.StopWorkerAsync(ownerA);
            await runtime.StopWorkerAsync(ownerB);
            await using var final = runtime.OpenRelay();
            var row = await final.Deliveries.SingleAsync();
            Assert.Equal(delivered.UpdatedUtc, row.UpdatedUtc);
            Assert.Equal(delivered.WorkId, row.WorkId);
            var attempts = await final.DeliveryAttempts.OrderBy(a => a.Sequence).ToListAsync();
            Assert.Equal(new[] { "Interrupted", "Acknowledged" }, attempts.Select(a => a.Outcome));
            Assert.Null(attempts[0].CompletedUtc);
            Assert.Equal(2, await final.OutboxMessages.CountAsync());
            await using var recipient = runtime.OpenReceiver();
            Assert.Single(await recipient.Effects.ToListAsync());
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task Failed_retry_transaction_keeps_started_attempt_and_recoverable_claim()
    {
        await using var runtime = new TestRuntime(infrastructure, retryBaseSeconds: 1);
        await runtime.StartAsync();
        await using var fault = await FaultReceiver.StartAsync(runtime);
        using var accepted = await runtime.SubmitAsync("retry-transaction");
        var id = await TestRuntime.IdAsync(accepted);
        var host = await runtime.StartWorkerAsync(fault.Address, start: false);
        await host.Services.GetRequiredService<OutboxPublisher>().PublishOneAsync(default);
        await using var bus = WorkerSettings.CreateBus(infrastructure.BusConnection);
        await using var receiver = bus.CreateReceiver("deliveries");
        var message = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(15));
        Assert.NotNull(message);
        var work = message.Body.ToObjectFromJson<WorkEnvelope>(EventContract.Json)!;
        await using var db = runtime.OpenRelay();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER FailRetryOutbox ON OutboxMessages AFTER INSERT AS
            BEGIN
                IF EXISTS (SELECT 1 FROM inserted WHERE AttemptNumber=2)
                    THROW 51003, 'Synthetic retry transaction failure', 1;
            END
            """);
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => host.Services.GetRequiredService<DeliveryProcessor>().ProcessAsync(work, default));
        Assert.Equal(51003, Assert.IsType<Microsoft.Data.SqlClient.SqlException>(error.InnerException).Number);
        Assert.Equal("Processing", (await db.Deliveries.AsNoTracking().SingleAsync()).Status);
        Assert.Equal("Started", (await db.DeliveryAttempts.SingleAsync()).Outcome);
        Assert.Single(await db.OutboxMessages.ToListAsync());
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER FailRetryOutbox");
        await db.Database.ExecuteSqlRawAsync("UPDATE Deliveries SET LeaseUntilUtc=DATEADD(second,-1,SYSUTCDATETIME())");
        await receiver.AbandonMessageAsync(message);
        await runtime.StartWorkerAsync();
        await runtime.WaitForAsync(id, "Delivered");
        Assert.Equal(2, await db.OutboxMessages.CountAsync());
        Assert.Equal(2, await db.DeliveryAttempts.CountAsync());
    }

    [Fact]
    public async Task Interrupted_last_slot_exhausts_without_another_HTTP_call()
    {
        await using var runtime = new TestRuntime(infrastructure, maxAttempts: 1);
        await runtime.StartAsync();
        using var accepted = await runtime.SubmitAsync("last-slot-crash");
        var id = await TestRuntime.IdAsync(accepted);
        using var gate = new BoundaryGate("AfterClaim");
        await runtime.StartWorkerAsync(boundary: gate.Observer);
        await gate.ArrivedAsync();
        await runtime.StopWorkerAsync();
        await using var db = runtime.OpenRelay();
        await db.Database.ExecuteSqlRawAsync("UPDATE Deliveries SET LeaseUntilUtc=DATEADD(second,-1,SYSUTCDATETIME())");
        await runtime.StartWorkerAsync();
        var failed = await runtime.WaitForAsync(id, "Failed");
        Assert.Equal("RetryExhausted", failed.LastOutcome);
        Assert.Equal("Unknown", failed.RemoteOutcome);
        Assert.Equal("Interrupted", (await db.DeliveryAttempts.SingleAsync()).Outcome);
        Assert.Single(await db.OutboxMessages.ToListAsync());
        await using var recipient = runtime.OpenReceiver();
        Assert.Empty(await recipient.Effects.ToListAsync());
    }
}
