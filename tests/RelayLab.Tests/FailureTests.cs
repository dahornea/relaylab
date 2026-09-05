using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RelayLab.Core;
using RelayLab.Receiver;
using Xunit;

namespace RelayLab.Tests;

[Collection("Infrastructure")]
[Trait("Category", "Integration")]
public sealed class FailureTests(Infrastructure infrastructure)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task One_failed_HTTP_attempt_is_durable_and_terminal_even_on_broker_redelivery(bool commitThenWithhold)
    {
        await using var runtime = new TestRuntime(infrastructure, maxAttempts: 1);
        await runtime.StartAsync();
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddDbContextFactory<ReceiverDb>(o => o.UseSqlServer(runtime.ReceiverConnection));
        builder.Services.AddTransient<ReceiverLedger>();
        await using var faultReceiver = builder.Build();
        faultReceiver.MapPost("/webhooks", async (HttpContext context, ReceiverLedger ledger) =>
        {
            if (!commitThenWithhold) { context.Response.StatusCode = 503; return; }
            var id = Guid.Parse(context.Request.Headers["X-RelayLab-Delivery-Id"].ToString());
            var request = await context.Request.ReadFromJsonAsync<EventRequest>(EventContract.Json, context.RequestAborted);
            Assert.True(await ledger.RecordAsync(id, request!, context.RequestAborted));
            committed.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted); }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        });
        await faultReceiver.StartAsync();
        try
        {
            using var accepted = await runtime.SubmitAsync("failure");
            var id = await TestRuntime.IdAsync(accepted);
            await runtime.StartWorkerAsync(new Uri(faultReceiver.Urls.Single() + "/webhooks"), timeoutSeconds: 2);
            if (commitThenWithhold) await committed.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var delivery = await runtime.WaitForAsync(id, "Failed");
            Assert.Equal(commitThenWithhold ? "Unknown" : "ResponseReceived", delivery.RemoteOutcome);
            Assert.Equal("RetryExhausted", delivery.LastOutcome);
            await runtime.RepublishAsync(id);
            await infrastructure.WaitUntilQueueEmptyAsync();
            await runtime.StopWorkerAsync();
            await using var db = runtime.OpenRelay();
            var attempt = Assert.Single(await db.DeliveryAttempts.ToListAsync());
            Assert.NotNull(attempt.CompletedUtc);
            Assert.Equal(commitThenWithhold ? null : 503, attempt.HttpStatus);
            Assert.Equal(commitThenWithhold ? "DeadlineExceeded" : "NonSuccessStatus", attempt.FailureCategory);
            await using var recipient = runtime.OpenReceiver();
            Assert.Equal(commitThenWithhold ? 1 : 0, await recipient.Effects.CountAsync());
            Assert.Equal(commitThenWithhold ? 1 : 0, await recipient.Receipts.CountAsync());
        }
        finally { await runtime.StopWorkerAsync(); await faultReceiver.StopAsync(); }
    }

    [Fact]
    public async Task Expired_processing_claim_is_recoverable_and_preserves_interrupted_history()
    {
        await using var runtime = new TestRuntime(infrastructure);
        await runtime.StartAsync();
        using var accepted = await runtime.SubmitAsync("interrupted");
        var id = await TestRuntime.IdAsync(accepted);
        Guid interrupted;
        await using (var db = runtime.OpenRelay())
        {
            var delivery = await db.Deliveries.SingleAsync();
            var now = await db.UtcNowAsync(default);
            delivery.Status = "Processing";
            delivery.LeaseOwner = Guid.NewGuid();
            delivery.LeaseUntilUtc = now.AddSeconds(-1);
            delivery.RemoteOutcome = "Unknown";
            interrupted = Guid.NewGuid();
            db.DeliveryAttempts.Add(new DeliveryAttempt
            { Id = interrupted, DeliveryId = id, WorkId = delivery.WorkId, StartedUtc = now.AddMinutes(-1) });
            await db.SaveChangesAsync();
        }
        using var status = await runtime.Api.GetAsync($"/deliveries/{id}");
        using var json = JsonDocument.Parse(await status.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.GetProperty("leaseExpired").GetBoolean());
        Assert.Contains("unknown", json.RootElement.GetProperty("warning").GetString());
        await runtime.StartWorkerAsync();
        await runtime.WaitForAsync(id, "Delivered");
        await infrastructure.WaitUntilQueueEmptyAsync();
        await runtime.StopWorkerAsync();
        await using var final = runtime.OpenRelay();
        var attempts = await final.DeliveryAttempts.AsNoTracking().ToListAsync();
        Assert.Equal(2, attempts.Count);
        var old = Assert.Single(attempts, a => a.Id == interrupted);
        Assert.Null(old.CompletedUtc);
        Assert.Equal("Interrupted", old.Outcome);
        Assert.Equal("Unknown", old.RemoteOutcome);
        Assert.Single(attempts, a => a.Outcome == "Acknowledged");
    }

    [Fact]
    public async Task Delivered_work_is_completed_again_without_another_HTTP_attempt()
    {
        await using var runtime = new TestRuntime(infrastructure);
        await runtime.StartAsync();
        using var accepted = await runtime.SubmitAsync("duplicate-work");
        var id = await TestRuntime.IdAsync(accepted);
        await runtime.StartWorkerAsync();
        await runtime.WaitForAsync(id, "Delivered");
        await runtime.RepublishAsync(id);
        await infrastructure.WaitUntilQueueEmptyAsync();
        await runtime.StopWorkerAsync();
        await using var db = runtime.OpenRelay();
        Assert.Single(await db.DeliveryAttempts.ToListAsync());
        await using var receiver = runtime.OpenReceiver();
        Assert.Single(await receiver.Effects.ToListAsync());
    }

    [Fact]
    public async Task Invalid_work_is_preserved_in_dead_letter_queue()
    {
        await using var runtime = new TestRuntime(infrastructure);
        await runtime.StartAsync();
        await runtime.StartWorkerAsync();
        await using var bus = RelayLab.Worker.WorkerSettings.CreateBus(infrastructure.BusConnection);
        await using var sender = bus.CreateSender("deliveries");
        await sender.SendMessageAsync(new ServiceBusMessage("{\"version\":900}") { MessageId = "invalid" });
        await using var deadLetters = bus.CreateReceiver("deliveries", new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter });
        ServiceBusReceivedMessage? observed = null;
        await TestRuntime.EventuallyAsync(async () =>
        {
            observed = (await deadLetters.PeekMessagesAsync(1, fromSequenceNumber: 0)).FirstOrDefault();
            return observed is not null;
        }, "Invalid envelope was not preserved in the dead-letter queue.");
        Assert.Equal("InvalidEnvelope", observed!.DeadLetterReason);
        Assert.Equal("invalid", (await deadLetters.PeekMessagesAsync(1, fromSequenceNumber: 0)).Single().MessageId);
    }
}
