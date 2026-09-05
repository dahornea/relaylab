# Testing and acceptance design

Use tests to resolve actual risks. A unit test can cover policy; SQL Server is required for transactions/uniqueness/claims; the Service Bus emulator is required for local messaging acceptance. Do not replace those dependencies with EF InMemory, SQLite or mocked SDK calls while claiming integration coverage.

## M1 required experiments

| Case | Stimulus | Observable oracle |
| --- | --- | --- |
| Delivery | Submit a valid new event | 202 after persistence, eventual Delivered, one receiver effect |
| Idempotency | Repeat the request, including concurrent callers | One Delivery, one initial outbox intent, one logical ID |
| Conflict | Reuse key with another documentId | 409 and original persisted values intact |
| Atomicity | Force acceptance to fail within the persistence transaction | Neither half of a new Delivery/outbox pair remains |
| Backlog | Accept while publisher is unavailable, then resume it | Intent remains in SQL and reaches the receiver |
| Receiver deduplication | Deliver the same ID twice, including receiver restart | A durable receipt and only one effect |
| M1 failure | Return non-2xx or lose the HTTP response | Sender records Failed with honest known/unknown remote outcome |

Use unit tests for small pure policy/validation behavior where useful. Assert publicly relevant results and persisted invariants, not private method ordering. No minimum test count or coverage percentage is a substitute for these cases.

## M2 failure matrix

1. Terminate the publisher after send succeeds but before outbox publication is marked. Re-publication must preserve logical identity and receiver effects.
2. Commit at the receiver and drop its response. Retry with the same DeliveryId; preserve one effect and record the ambiguous first outcome.
3. Terminate the worker after recipient acknowledgement but before sender completion. Expired claims and subsequent work must recover.
4. Run two workers and inject duplicate/stale work. Check lease ownership, eligible attempt identity and durable state.
5. Fail transiently then recover. Inspect persisted next-attempt eligibility, budget and eventual result.
6. Exhaust the configured budget. Inspect application failure and broker dead-letter handling; explicitly replay and repeat the replay request.
7. Interrupt broker connectivity without deleting its stored state. Reconnect and recover outstanding accepted intent.

Use narrow internal test seams or external process/network control with synchronization at a defined boundary. Avoid arbitrary sleeps and production-accessible fault switches. Bound waits, surface timeouts as failures and always clean up owned test resources.

The Service Bus emulator's data disappears on restart and it lacks cloud features. Run scenarios sharing it sequentially. A broker container restart is not an Azure durability experiment. Testcontainers may manage independent containers, but do not add unneeded parallel load to the shared emulator.

## Test environment and evidence

Select one current compatible xUnit runner and record actual invocation. The first ordinary restore creates lock files; later restore uses locked mode. Use pinned tested dependency images in Compose and automated tests. A missing Docker engine is a stated integration blocker, not permission to report a mocked substitute as accepted.

During development, run focused tests. On the final milestone candidate, run its full acceptance and one fresh-input demo. Include intended untracked source files in any clean temporary copy; preserve tracked/generated-input boundaries. Do not erase the owner's repository to obtain a clean state.

Keep logs and test reports under ignored artifacts/; publish compact factual summaries in docs/STATUS.md. When tests fail, retain diagnostic output. CI and cloud runs need their own URLs/environment records. M3 adds a real Azure smoke/failure/rollback check; local emulator tests do not establish cloud identity or deployment correctness.

## Implemented M1 entry points

`pwsh -NoProfile -File ./eng/verify.ps1` runs locked restore, Release build, xUnit/VSTest, and a fresh-input container demo. It captures a SHA-256 input manifest without requiring staging, includes intended untracked files, creates a unique Compose project with random loopback ports, and cleans up that project's volume and generated secret. The GitHub workflow invokes this same entry point; remote execution requires an authorized push/run.

`ConnectivityTests` checks a real SQL SELECT and Service Bus send/PeekLock/complete. `AcceptanceTests` uses actual Kestrel API/receiver hosts against SQL to verify concurrent requests, typed identity conflicts, atomic rollback, a backlog across API restart, recipient deduplication across receiver host restart, and validation. The rollback test observes SQL error 51000 through a test-only interceptor: the trigger must see the Delivery before injecting that error; another 503 does not satisfy the oracle. `FailureTests` runs the real Worker against the emulator and controlled test-only HTTP endpoints. A commit barrier in the receiver is reached before withholding the response; the sender must report Failed/Unknown with one durable receiver effect. Terminal redelivery, recoverable expired SQL claims and preserved malformed-message dead letters are also checked. No fault endpoint exists in the shipping API or sample receiver.

The receiver restart test recreates its HTTP host and DI services against retained SQL, proving no in-memory deduplication dependency. M1's non-2xx/timeout test now explicitly configures a one-attempt budget. Test fixtures own disposable containers/databases and serialize shared-emulator scenarios. Failure logs are under ignored `TestResults/containers`; CI uploads selected diagnostics rather than generated `.env` files or complete fresh-copy directories.

## Implemented M2 experiments

- `RetryTests`: persisted backoff and new work identity after a committed effect/aborted response; receiver/worker restart; premature and stale signals; bounded exhaustion; concurrent repeated and distinct replay keys; repeat after Delivered; replay rollback at outbox insertion; cursor pagination and invalid requests.
- `RecoveryTests`: a publisher held after successful real send is canceled or resumed stale after lease expiry and another publisher's commit; both signals use the same MessageId. Worker boundaries after Started, HTTP acknowledgement and result commit are interrupted with cancellation. Competing workers process duplicate signals, expire the held owner's lease and commit recovery before allowing that stale owner to attempt completion. An injected SQL retry-insert error 51003 leaves the original Started claim intact; recovery proceeds after the trigger is removed. An interrupted last slot exhausts without another HTTP call.
- `BrokerRecoveryTests`: real PeekLock abandon cycles reach `MaxDeliveryCountExceeded`; the DLQ item is preserved while SQL republishes the same current work and completes delivery. A test-owned TCP forwarder closes/rejects broker connections without restarting the emulator. Actual sends fail while closed, a queued sentinel survives, SQL retains new intent and delivery completes after reconnect.
- `TelemetryTests`: a confirmed unavailable OTLP endpoint does not block real acceptance, delivery or durable status. `RetryPolicyTests` checks the bounded classifier/backoff; these pure tests are distinct from infrastructure acceptance.
- Internal `WorkerBoundary` hooks are inert without test DI configuration. They have no production HTTP/configuration fault switches. Gates expose explicit TaskCompletionSource barriers; SQL eligibility is moved far into the future for early-signal checks and explicitly made eligible afterward. Lease expiry uses SQL time. Bounded polling observes results instead of asserting elapsed wall-clock durations. Fixtures stop each owned worker and remove only their own databases/containers.

Broker settlement/cleanup waits derive from the configured queue lock duration plus a 30-second margin (90 seconds with PT1M). A canceled owner's signal may remain locked after StopAsync; an empty receive is not proof of an empty queue. Cleanup peeks for such signals and drains them before deleting the owned database, preventing cross-case contamination.

`eng/demo.ps1` defaults to the Recovery scenario. In addition to the normal M1 flow, it exhausts three attempts, repeats a replay request, observes the receiver's committed effect, kills the actual worker container with SIGKILL, restarts receiver/worker, and checks the final five-attempt history/one effect. It fails if SQL did not remain Started at the kill boundary. This is actual Linux-process interruption evidence; the test-host boundary cases are controlled cancellation experiments and are not described as OS kills.

The demo queries the pinned Aspire viewer's actual trace API, checking all intended lifecycle span names, the three services and one correlated trace ID. It waits for pre-kill spans to be ingested because an exporter buffer is not durable. Browser Metrics/Table inspection verifies numeric exported metrics separately; the HTTP trace API does not prove metric ingestion. Final executed counts, commands and any unverified behavior belong in STATUS.
