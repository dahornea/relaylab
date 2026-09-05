using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace RelayLab.Tests;

[Collection("Infrastructure")]
[Trait("Category", "Integration")]
public sealed class RetryTests(Infrastructure infrastructure)
{
    [Fact]
    public async Task Invalid_replay_requests_and_history_cursors_do_not_change_delivery()
    {
        await using var runtime = new TestRuntime(infrastructure);
        await runtime.StartAsync();
        using var accepted = await runtime.SubmitAsync("replay-validation");
        var id = await TestRuntime.IdAsync(accepted);
        using var missing = await runtime.Api.PostAsync($"/deliveries/{id}/replay", null);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/deliveries/{id}/replay") { Content = new StringContent("{}") };
        request.Headers.Add("Idempotency-Key", "valid-key");
        using var body = await runtime.Api.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, body.StatusCode);
        using var unknown = await runtime.ReplayAsync(Guid.NewGuid(), "unknown-id");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        using var active = await runtime.ReplayAsync(id, "active-key");
        Assert.Equal(HttpStatusCode.Conflict, active.StatusCode);
        using var invalidPage = await runtime.Api.GetAsync($"/deliveries/{id}?after=-1&limit=51");
        Assert.Equal(HttpStatusCode.BadRequest, invalidPage.StatusCode);
        await using var db = runtime.OpenRelay();
        Assert.Empty(await db.ReplayRequests.ToListAsync());
        Assert.Equal(0, (await db.Deliveries.SingleAsync()).Generation);
        Assert.Single(await db.OutboxMessages.ToListAsync());
    }

    [Fact]
    public async Task Ambiguous_effect_retries_after_persisted_eligibility_and_receiver_restart()
    {
        await using var runtime = new TestRuntime(infrastructure, retryBaseSeconds: 30);
        await runtime.StartAsync();
        await using var fault = await FaultReceiver.StartAsync(runtime);
        fault.CommitThenAbort = true;
        using var gate = new BoundaryGate("AfterTransition");
        using var accepted = await runtime.SubmitAsync("ambiguous-retry");
        var id = await TestRuntime.IdAsync(accepted);
        await runtime.StartWorkerAsync(fault.Address, boundary: gate.Observer);
        var oldWork = await gate.ArrivedAsync();
        await runtime.StopWorkerAsync(); // cancellation at a committed retry boundary, before broker settlement
        await using (var db = runtime.OpenRelay())
        {
            var delivery = await db.Deliveries.SingleAsync();
            var first = Assert.Single(await db.DeliveryAttempts.ToListAsync());
            Assert.Equal("Unknown", first.RemoteOutcome);
            Assert.Equal("TransportFailure", first.Outcome);
            Assert.Equal("Pending", delivery.Status);
            Assert.Equal(2, delivery.AttemptNumber);
            Assert.NotEqual(oldWork, delivery.WorkId);
            Assert.Equal(30, (delivery.NextAttemptUtc - first.CompletedUtc!.Value).TotalSeconds);
            Assert.Equal(delivery.NextAttemptUtc, (await db.OutboxMessages.SingleAsync(o => o.Id == delivery.WorkId)).NextPublishUtc);
            // Pin eligibility well into the future so early-signal assertions never depend on machine speed.
            delivery.NextAttemptUtc = (await db.UtcNowAsync(default)).AddHours(1);
            await db.SaveChangesAsync();
        }
        await runtime.RestartReceiverAsync();
        var worker = await runtime.StartWorkerAsync();
        await runtime.RepublishAsync(id, oldWork);
        await runtime.RepublishAsync(id);
        await infrastructure.WaitUntilQueueEmptyAsync();
        await using (var db = runtime.OpenRelay()) Assert.Equal(1, await db.DeliveryAttempts.CountAsync());
        await MakeEligibleAsync(runtime, id);
        await runtime.WaitForAsync(id, "Delivered");
        await runtime.StopWorkerAsync(worker);
        await using var final = runtime.OpenRelay();
        var attempts = await final.DeliveryAttempts.OrderBy(a => a.Sequence).ToListAsync();
        Assert.Equal(2, attempts.Count);
        Assert.Equal(2, attempts.Select(a => a.WorkId).Distinct().Count());
        Assert.All(attempts, a => Assert.Equal(id, a.DeliveryId));
        await using var receiver = runtime.OpenReceiver();
        Assert.Single(await receiver.Effects.ToListAsync());
        Assert.Single(await receiver.Receipts.ToListAsync());
    }

    [Fact]
    public async Task Exhaustion_and_concurrent_replay_preserve_identity_budget_and_paginated_history()
    {
        await using var runtime = new TestRuntime(infrastructure, retryBaseSeconds: 1);
        await runtime.StartAsync();
        await using var fault = await FaultReceiver.StartAsync(runtime);
        using var accepted = await runtime.SubmitAsync("exhaustion");
        var id = await TestRuntime.IdAsync(accepted);
        await runtime.StartWorkerAsync(fault.Address);
        var failed = await runtime.WaitForAsync(id, "Failed");
        Assert.Equal("RetryExhausted", failed.LastOutcome);
        await runtime.StopWorkerAsync();
        await using (var db = runtime.OpenRelay())
        {
            var attempts = await db.DeliveryAttempts.OrderBy(a => a.Sequence).ToListAsync();
            Assert.Equal(new[] { 1, 2, 3 }, attempts.Select(a => a.AttemptNumber));
            Assert.All(attempts, a => Assert.Equal(503, a.HttpStatus));
            Assert.Equal(3, await db.OutboxMessages.CountAsync());
            var rows = await db.OutboxMessages.OrderBy(o => o.AttemptNumber).ToListAsync();
            Assert.Equal(attempts[0].CompletedUtc, rows[1].CreatedUtc);
        }
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => runtime.ReplayAsync(id, "replay-one")));
        try
        {
            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Accepted);
            Assert.Equal(7, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
            var bodies = await Task.WhenAll(responses.Select(r => r.Content.ReadAsStringAsync()));
            Assert.Single(bodies.Distinct());
        }
        finally { foreach (var response in responses) response.Dispose(); }
        using var conflict = await runtime.ReplayAsync(id, "new-key-while-active");
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        await runtime.StartWorkerAsync();
        await runtime.WaitForAsync(id, "Delivered");
        using var repeat = await runtime.ReplayAsync(id, "replay-one");
        Assert.Equal(HttpStatusCode.OK, repeat.StatusCode);
        using var repeatedBody = JsonDocument.Parse(await repeat.Content.ReadAsStringAsync());
        Assert.Equal(1, repeatedBody.RootElement.GetProperty("generation").GetInt32());
        using var terminalConflict = await runtime.ReplayAsync(id, "new-key-after-delivered");
        Assert.Equal(HttpStatusCode.Conflict, terminalConflict.StatusCode);
        var sequences = new List<long>();
        long? cursor = 0;
        while (cursor is not null)
        {
            using var status = await runtime.Api.GetAsync($"/deliveries/{id}?limit=1&after={cursor}");
            using var json = JsonDocument.Parse(await status.Content.ReadAsStringAsync());
            Assert.Equal(4, json.RootElement.GetProperty("attemptCount").GetInt32());
            sequences.Add(json.RootElement.GetProperty("attempts")[0].GetProperty("sequence").GetInt64());
            var next = json.RootElement.GetProperty("nextCursor");
            cursor = next.ValueKind == JsonValueKind.Null ? null : next.GetInt64();
        }
        Assert.Equal(4, sequences.Distinct().Count());
        Assert.Equal(sequences.Order(), sequences);
        await using var final = runtime.OpenRelay();
        Assert.Single(await final.ReplayRequests.ToListAsync());
        Assert.Equal(4, await final.OutboxMessages.CountAsync());
        await using var receiver = runtime.OpenReceiver();
        Assert.Single(await receiver.Effects.ToListAsync());
    }

    [Fact]
    public async Task Replay_outbox_failure_rolls_back_receipt_and_generation_then_distinct_keys_have_one_winner()
    {
        var observer = new ReplaySqlFailureObserver();
        await using var runtime = new TestRuntime(infrastructure, observer, maxAttempts: 1);
        await runtime.StartAsync();
        await using var fault = await FaultReceiver.StartAsync(runtime);
        fault.Status = 400;
        using var accepted = await runtime.SubmitAsync("replay-atomic");
        var id = await TestRuntime.IdAsync(accepted);
        await runtime.StartWorkerAsync(fault.Address);
        Assert.Equal("NonRetryableFailure", (await runtime.WaitForAsync(id, "Failed")).LastOutcome);
        await runtime.StopWorkerAsync();
        await using var db = runtime.OpenRelay();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER FailReplayOutbox ON OutboxMessages AFTER INSERT AS
            BEGIN
                IF EXISTS (SELECT 1 FROM inserted i JOIN Deliveries d ON d.Id=i.DeliveryId WHERE i.Generation=1 AND d.Generation=1)
                    THROW 51002, 'Synthetic replay transaction failure', 1;
            END
            """);
        using var rejected = await runtime.ReplayAsync(id, "same-after-rollback");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, rejected.StatusCode);
        Assert.Equal(51002, observer.ErrorNumber);
        Assert.Empty(await db.ReplayRequests.ToListAsync());
        Assert.Equal(0, (await db.Deliveries.AsNoTracking().SingleAsync()).Generation);
        Assert.Single(await db.OutboxMessages.ToListAsync());
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER FailReplayOutbox");
        var responses = await Task.WhenAll(runtime.ReplayAsync(id, "same-after-rollback"), runtime.ReplayAsync(id, "competing-key"));
        try
        {
            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Accepted);
            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Conflict);
        }
        finally { foreach (var response in responses) response.Dispose(); }
        Assert.Single(await db.ReplayRequests.ToListAsync());
        Assert.Equal(2, await db.OutboxMessages.CountAsync());
    }

    internal static async Task MakeEligibleAsync(TestRuntime runtime, Guid id)
    {
        await using var db = runtime.OpenRelay();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE Deliveries SET NextAttemptUtc=SYSUTCDATETIME() WHERE Id={id} AND Status='Pending'");
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE OutboxMessages SET NextPublishUtc=SYSUTCDATETIME() WHERE Id=(SELECT WorkId FROM Deliveries WHERE Id={id})");
    }

    private sealed class ReplaySqlFailureObserver : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        public int? ErrorNumber { get; private set; }
        public override Task SaveChangesFailedAsync(Microsoft.EntityFrameworkCore.Diagnostics.DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            if (eventData.Exception.GetBaseException() is Microsoft.Data.SqlClient.SqlException error) ErrorNumber = error.Number;
            return Task.CompletedTask;
        }
    }
}
