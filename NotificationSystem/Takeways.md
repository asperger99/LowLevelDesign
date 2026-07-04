# Notification System LLD — Takeaways

## What Went Well

- Caught early that a single `status` field on `Notification` was wrong — correctly split into per-channel status via `NotificationDelivery`
- Correctly identified that separate worker pools per channel prevent one slow provider from blocking others
- Idempotency key design `(notificationId, channel)` was the right call — composite, not just messageId alone
- Mentioned `externalProviderId` on `NotificationDelivery` — shows awareness of production debugging needs (correlate with Twilio/SES logs)
- Added `ProcessNotificationDlq()` on IWorker without being prompted — staff-level detail
- Pushed back correctly on the LLD vs HLD scope question — good instinct to challenge the interviewer

---

## Mistakes Made and Corrections

**`Notification.channels` typed as single ChannelType**
Should have been `List<ChannelType>` from the start. Multi-channel was FR #1. Always check collection vs scalar when the requirement says "multiple."

**`SendNotification() : void`**
Returning void meant the caller had no way to query status later, breaking FR #6. In async systems, always return at minimum a correlation ID so the caller can follow up.

**`INotificationProvider.Send(Notification)` instead of `Send(NotificationDelivery, Notification)`**
A provider handles one channel — passing the full Notification made it responsible for figuring out which channel it owns. Passing `NotificationDelivery` is cleaner — it already carries channelType, status, and retryCount.

**`IWorkerFactory` listed unnecessarily**
Workers are long-running consumers, not created per request. A factory that spins up a new worker per message is wrong. Workers are bootstrapped once at startup — factory pattern does not fit here.

**Check-then-insert for idempotency**
Initial instinct was `if (!map.ContainsKey) map.Add` — that is a race condition. The fix is `ConcurrentDictionary.TryAdd` which is atomic. One call replaces the check entirely.

---

## Concepts to Drill

**Compensating transaction pattern**
Used when you have two non-atomic operations (persist + enqueue). Persist first, then enqueue. On enqueue failure, delete the persisted record. Weakness: the rollback can also fail. Better approach is a recovery job that re-enqueues stuck `Enqueued` status records beyond a time threshold.

**Outbox Pattern**
Production-grade solution to the persist + enqueue atomicity problem. Write the notification and the outbox event in the same DB transaction. A separate poller reads the outbox and enqueues — never loses a message even on crash. Worth mentioning in interviews even if not implementing it fully.

**Re-enqueue vs Thread.Sleep for retry**
Never sleep inside a worker loop — it blocks that thread from processing other messages. Always re-enqueue to a retry queue with a delay. In production this is a queue visibility timeout (SQS, RabbitMQ TTL). In-memory equivalent is Task.Delay before re-enqueue.

**Busy-wait on empty queue**
A worker that loops tightly on an empty queue pegs the CPU. Always add a small Task.Delay (e.g. 100ms) when TryDequeue returns false.

**putIfAbsent / TryAdd mental model**
In any concurrent idempotency check — whether DB, Redis, or ConcurrentDictionary — the check and the insert must be one atomic operation. In C# it is `TryAdd`. In Postgres it is `INSERT ... ON CONFLICT DO NOTHING`. In Redis it is `SET NX`. Know all three.

---

## Interview Meta Takeaways

**List FRs before jumping to design**
Took a few rounds to get to a complete FR list. In a real interview, write all FRs on the board before touching any class. Interviewers reward thoroughness upfront.

**Say the tradeoff, then pick**
On retry policy (per-worker vs per-message) the question was asked twice before getting an answer. In interviews, always state both options with one sentence of tradeoff each, then commit. Silence or deflection reads as uncertainty.

**NFRs in LLD are brief, not skipped**
The instinct to skip NFRs in LLD was wrong. You state them in 30 seconds to lock in constraints (async, durable, etc.) because those constraints directly shape your class design. Retry queue, recovery job, and IQueue abstraction all came from NFR decisions.

**Name the pattern explicitly**
When you use Strategy for providers and retry, say "this is Strategy pattern — one interface, multiple implementations, swappable without touching the caller." Interviewers at staff level want to hear you name and justify patterns, not just use them implicitly.

**Entity splitting is a design decision worth calling out**
The `Notification` to `Notification + NotificationDelivery` split was a non-trivial decision. Stating it explicitly — "I'm splitting because one notification can have independent per-channel status" — shows design reasoning, not just instinct.