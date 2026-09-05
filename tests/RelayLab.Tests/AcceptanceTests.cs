using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace RelayLab.Tests;

[Collection("Infrastructure")]
[Trait("Category", "Integration")]
public sealed class AcceptanceTests(Infrastructure infrastructure)
{
    [Fact]
    public async Task Concurrent_retries_preserve_one_intent_and_ordinal_identity()
    {
        await using var runtime = new TestRuntime(infrastructure);
        await runtime.StartAsync();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = Enumerable.Range(0, 12).Select(async _ =>
        {
            await gate.Task;
            return await runtime.SubmitAsync("same-key");
        }).ToArray();
        gate.SetResult();
        var responses = await Task.WhenAll(requests);
        try
        {
            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Accepted);
            Assert.Equal(11, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
            var ids = await Task.WhenAll(responses.Select(TestRuntime.IdAsync));
            Assert.Single(ids.Distinct());
            using var reordered = new HttpRequestMessage(HttpMethod.Post, "/events")
            {
                Content = new StringContent("{\"data\":{\"documentId\":\"doc-001\"},\"eventType\":\"document.ready\",\"destinationId\":\"demo\"}", Encoding.UTF8, "application/json")
            };
            reordered.Headers.Add("Idempotency-Key", "same-key");
            using var repeat = await runtime.Api.SendAsync(reordered);
            Assert.Equal(HttpStatusCode.OK, repeat.StatusCode);
            Assert.Equal(ids[0], await TestRuntime.IdAsync(repeat));
            using var conflict = await runtime.SubmitAsync("same-key", "DOC-001");
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            using var problem = JsonDocument.Parse(await conflict.Content.ReadAsStringAsync());
            Assert.Equal("idempotency_conflict", problem.RootElement.GetProperty("code").GetString());
            await using (var db = runtime.OpenRelay())
            {
                var delivery = Assert.Single(await db.Deliveries.ToListAsync());
                Assert.Equal("doc-001", delivery.DocumentId);
                var intent = Assert.Single(await db.OutboxMessages.ToListAsync());
                Assert.Equal(delivery.WorkId, intent.Id);
                Assert.Equal(delivery.Id, intent.DeliveryId);
            }
            using var differentCase = await runtime.SubmitAsync("SAME-key");
            Assert.Equal(HttpStatusCode.Accepted, differentCase.StatusCode);
            Assert.NotEqual(ids[0], await TestRuntime.IdAsync(differentCase));
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [Fact]
    public async Task Outbox_insert_failure_rolls_back_a_delivery_already_written_to_SQL()
    {
        var observer = new SqlFailureObserver();
        await using var runtime = new TestRuntime(infrastructure, observer);
        await runtime.StartAsync();
        await using var db = runtime.OpenRelay();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER FailOutbox ON OutboxMessages AFTER INSERT AS
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM Deliveries d JOIN inserted i ON i.DeliveryId=d.Id)
                    THROW 51001, 'Expected delivery write before outbox', 1;
                THROW 51000, 'Injected outbox failure after delivery write', 1;
            END
            """);
        try
        {
            using var failed = await runtime.SubmitAsync("rollback-key");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
            // 51000 proves the trigger observed the already-written delivery. A different 503 is not this experiment.
            Assert.Equal(51000, observer.ErrorNumber);
            Assert.Empty(await db.Deliveries.AsNoTracking().ToListAsync());
            Assert.Empty(await db.OutboxMessages.AsNoTracking().ToListAsync());
        }
        finally { await db.Database.ExecuteSqlRawAsync("DROP TRIGGER FailOutbox"); }
        using var accepted = await runtime.SubmitAsync("rollback-key");
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        Assert.Single(await db.Deliveries.AsNoTracking().ToListAsync());
        Assert.Single(await db.OutboxMessages.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Accepted_backlog_survives_API_restart_and_delivers_when_worker_starts()
    {
        await using var runtime = new TestRuntime(infrastructure);
        await runtime.StartAsync();
        using var response = await runtime.SubmitAsync("backlog");
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var id = await TestRuntime.IdAsync(response);
        Assert.Equal($"/deliveries/{id}", response.Headers.Location?.ToString());
        await using (var db = runtime.OpenRelay())
        {
            Assert.Equal("Pending", (await db.Deliveries.SingleAsync()).Status);
            Assert.Null((await db.OutboxMessages.SingleAsync()).PublishedUtc);
        }
        await using (var db = runtime.OpenReceiver()) Assert.Empty(await db.Effects.ToListAsync());
        await runtime.RestartApiAsync();
        using var repeat = await runtime.SubmitAsync("backlog");
        Assert.Equal(HttpStatusCode.OK, repeat.StatusCode);
        Assert.Equal(id, await TestRuntime.IdAsync(repeat));
        await runtime.StartWorkerAsync();
        await runtime.WaitForAsync(id, "Delivered");
        await runtime.StopWorkerAsync();
        using var status = await runtime.Api.GetAsync($"/deliveries/{id}");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        using var json = JsonDocument.Parse(await status.Content.ReadAsStringAsync());
        Assert.Equal("Delivered", json.RootElement.GetProperty("status").GetString());
        Assert.Equal("Acknowledged", json.RootElement.GetProperty("remoteOutcome").GetString());
        Assert.Single(json.RootElement.GetProperty("attempts").EnumerateArray());
        await using (var db = runtime.OpenRelay())
        {
            var attempt = Assert.Single(await db.DeliveryAttempts.ToListAsync());
            Assert.Equal(200, attempt.HttpStatus);
            Assert.NotNull(attempt.CompletedUtc);
            Assert.NotNull((await db.OutboxMessages.SingleAsync()).PublishedUtc);
        }
        await using (var db = runtime.OpenReceiver())
        {
            Assert.Equal(id, Assert.Single(await db.Receipts.ToListAsync()).DeliveryId);
            Assert.Equal(id, Assert.Single(await db.Effects.ToListAsync()).DeliveryId);
        }
    }

    [Fact]
    public async Task Receiver_deduplicates_concurrent_calls_and_restart_then_rejects_conflict()
    {
        await using var runtime = new TestRuntime(infrastructure);
        await runtime.StartAsync();
        var id = Guid.NewGuid();
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => runtime.DeliverAsync(id)));
        try { Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode)); }
        finally { foreach (var response in responses) response.Dispose(); }
        await runtime.RestartReceiverAsync();
        using var duplicate = await runtime.DeliverAsync(id);
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        using var conflict = await runtime.DeliverAsync(id, "other-document");
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        await using var db = runtime.OpenReceiver();
        Assert.Equal("doc-001", Assert.Single(await db.Receipts.ToListAsync()).DocumentId);
        Assert.Equal("doc-001", Assert.Single(await db.Effects.ToListAsync()).DocumentId);
    }

    [Theory]
    [InlineData("{\"destinationId\":\"demo\",\"eventType\":\"document.ready\",\"data\":{\"documentId\":\"doc\"},\"url\":\"http://elsewhere\"}")]
    [InlineData("{\"destinationId\":\"demo\",\"eventType\":\"document.ready\",\"data\":{\"documentId\":\"doc\",\"extra\":true}}")]
    [InlineData("{\"destinationId\":\"elsewhere\",\"eventType\":\"document.ready\",\"data\":{\"documentId\":\"doc\"}}")]
    [InlineData("null")]
    public async Task Invalid_JSON_contracts_are_rejected_without_persistence(string body)
    {
        await using var runtime = new TestRuntime(infrastructure);
        await runtime.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/events") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("Idempotency-Key", "valid-key");
        using var response = await runtime.Api.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var db = runtime.OpenRelay();
        Assert.Empty(await db.Deliveries.ToListAsync());
    }

    [Fact]
    public async Task HTTP_bounds_and_identifier_errors_use_problem_responses()
    {
        await using var runtime = new TestRuntime(infrastructure);
        await runtime.StartAsync();
        using var missingKey = await runtime.Api.PostAsJsonAsync("/events", TestRuntime.Event());
        Assert.Equal(HttpStatusCode.BadRequest, missingKey.StatusCode);
        using var wrongType = await runtime.Api.PostAsync("/events", new StringContent("{}"));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, wrongType.StatusCode);
        using var oversized = await runtime.Api.PostAsync("/events", new StringContent(new string(' ', 4097), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
        using var malformed = await runtime.Api.GetAsync("/deliveries/not-a-guid");
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Equal("application/problem+json", malformed.Content.Headers.ContentType?.MediaType);
        using var missing = await runtime.Api.GetAsync($"/deliveries/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    private sealed class SqlFailureObserver : SaveChangesInterceptor
    {
        public int? ErrorNumber { get; private set; }
        public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            if (eventData.Exception.GetBaseException() is SqlException error) ErrorNumber = error.Number;
            return Task.CompletedTask;
        }
    }
}
