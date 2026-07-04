# Notification System — Pre Coding Draft

## Functional Requirements

1. Multi-channel support — Email, SMS, Push
2. Channel selection — explicit by caller, or fallback to user preferences if none provided
3. Retry on failure — max retry cutoff, status retained after exhaustion
4. Idempotency — per channel per notification, no double sends
5. Extensible — adding a new channel should not touch existing code
6. Status tracking — caller can query delivery status per channel

---

## Non Functional Requirements

- Async, queue based processing — caller gets immediate ack, workers process in background
- Durable — notification persisted before ack so a crash does not lose it
- Channel isolation — one slow provider should not block other channels
- High throughput — separate queue and worker pool per channel

---

## Core Design Decisions

**Sync vs Async**
Async. Caller fires and forgets after receiving notificationId. Workers process in background.

**Per channel status vs per notification status**
Per channel. One notification going to Email + SMS can have Email = Sent and SMS = Failed independently. This led to splitting into two entities: Notification and NotificationDelivery.

**Idempotency key**
Composite key of (notificationId, channel). Enforced via ConcurrentDictionary.TryAdd — one atomic call, no check-then-insert race.

**Worker design**
Separate worker per channel — Email, SMS, Push each have their own main queue, retry queue, and DLQ. Prevents one slow provider from blocking others.

**Retry approach**
RetryPolicy is per worker, injected at construction via Strategy pattern. On failure, worker re-enqueues to a separate retry queue with a computed delay — no Thread.Sleep inside the worker loop. After max retries, delivery moves to DLQ and status is marked Failed.

**Atomicity of persist + enqueue**
Persist first, then enqueue. If enqueue fails, a background recovery job re-enqueues any NotificationDelivery records stuck in Enqueued status beyond a time threshold. No rollback needed.

---

## Entities

```
Notification
- Id : Guid
- UserId : Guid
- Subject : string
- Body : string
- Channels : List<ChannelType>

NotificationDelivery
- Id : Guid
- NotificationId : Guid
- Channel : ChannelType
- Status : DeliveryStatus
- ExternalProviderId : string
- RetryCount : int
- LastTriedAt : DateTime

User
- Id : Guid
- Name : string
- Preferences : List<ChannelType>

RetrySetting
- MaxRetryCount : int
- RetryDelayMs : int
- RetryType : RetryType

NotificationJob  [queued item]
- Notification : Notification
- Delivery : NotificationDelivery
```

---

## Enums

```
ChannelType       — Email, Sms, Push
DeliveryStatus    — Pending, Enqueued, Retrying, Sent, Failed
RetryType         — FixedWindow, ExponentialBackoff
```

---

## Interfaces and Classes

**Queue**
```
IQueue<T>
- Enqueue(T item) : void
- TryDequeue(out T item) : bool
- Count : int

InMemoryQueue<T> : IQueue<T>
- backed by ConcurrentQueue<T>
```

**Retry Strategy (Strategy pattern)**
```
IRetryStrategy
- ShouldRetry(retryCount: int) : bool
- GetNextDelayMs(retryCount: int) : int

ExponentialBackoffRetryStrategy : IRetryStrategy
- constructor(RetrySetting settings)

FixedWindowRetryStrategy : IRetryStrategy
- constructor(RetrySetting settings)
```

**Provider (Strategy pattern)**
```
INotificationProvider
- Channel : ChannelType
- Send(delivery: NotificationDelivery, notification: Notification) : bool

EmailProvider : INotificationProvider
SmsProvider : INotificationProvider
PushProvider : INotificationProvider
```

**Factory**
```
INotificationProviderFactory
- GetProvider(channel: ChannelType) : INotificationProvider

NotificationProviderFactory : INotificationProviderFactory
- constructor(IEnumerable<INotificationProvider> providers)
```

**Repository**
```
INotificationRepository
- AddNotification(notification: Notification) : Guid
- AddNotificationDelivery(delivery: NotificationDelivery) : bool   // false = duplicate
- UpdateNotificationDelivery(delivery: NotificationDelivery) : void

IUserRepository
- GetUserPreferences(userId: Guid) : List<ChannelType>
```

**Worker**
```
IWorker
- Start(ct: CancellationToken) : void

NotificationWorker : IWorker
- _channel : ChannelType
- _mainQueue : IQueue<NotificationJob>
- _retryQueue : IQueue<NotificationJob>
- _dlqQueue : IQueue<NotificationJob>
- _providerFactory : INotificationProviderFactory
- _retryStrategy : IRetryStrategy
- _repository : INotificationRepository
```

**Service**
```
INotificationService
- SendNotification(notification: Notification) : Guid   // returns notificationId

NotificationService : INotificationService
- creates all queues at construction time
- passes same queue references to workers
- workers start consuming immediately on construction
```

---

## Flow — SendNotification

1. Caller sends Notification with optional Channels list
2. If Channels is empty, resolve from user preferences via IUserRepository
3. Persist Notification via INotificationRepository.AddNotification
4. For each channel:
   a. Create NotificationDelivery with status = Enqueued
   b. Call AddNotificationDelivery — returns false if duplicate, skip
   c. Enqueue NotificationJob to that channel's main queue
5. Return notificationId to caller — done

---

## Flow — Worker Processing

1. Worker pulls NotificationJob off main queue
2. Calls INotificationProvider.Send
3. On success — update delivery status to Sent
4. On failure:
   a. Increment retryCount
   b. If ShouldRetry — compute delay, update status to Retrying, re-enqueue to retry queue
   c. If max retries hit — update status to Failed, enqueue to DLQ

---

## Design Patterns Used

| Pattern    | Where                                        |
|------------|----------------------------------------------|
| Strategy   | INotificationProvider, IRetryStrategy        |
| Factory    | INotificationProviderFactory                 |
| Repository | INotificationRepository, IUserRepository     |
| Worker     | NotificationWorker consuming shared queues   |