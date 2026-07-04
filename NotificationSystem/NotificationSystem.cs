using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;


namespace LowLevelDesign.NotificationSystem;

// -------------------------
// Enums
// -------------------------

public enum ChannelType { Email, Sms, Push }

public enum DeliveryStatus { Pending, Enqueued, Retrying, Sent, Failed }

public enum RetryType { FixedWindow, ExponentialBackoff }

// -------------------------
// Entities
// -------------------------

public class Notification
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string Subject { get; set; }
    public string Body { get; set; }
    public List<ChannelType> Channels { get; set; } = new();
}

public class NotificationDelivery
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid NotificationId { get; set; }
    public ChannelType Channel { get; set; }
    public DeliveryStatus Status { get; set; }
    public string ExternalProviderId { get; set; }
    public int RetryCount { get; set; }
    public DateTime? LastTriedAt { get; set; }
}

public class User
{
    public Guid Id { get; set; }
    public string Name { get; set; }
    public List<ChannelType> Preferences { get; set; } = new();
}

public class RetrySetting
{
    public int MaxRetryCount { get; set; }
    public int RetryDelayMs { get; set; }
    public RetryType RetryType { get; set; }
}

// Worker pulls this off the queue — has everything it needs in one object
public class NotificationJob
{
    public Notification Notification { get; set; }
    public NotificationDelivery Delivery { get; set; }
}

// -------------------------
// Queue
// -------------------------

public interface IQueue<T>
{
    void Enqueue(T item);
    bool TryDequeue(out T item);
    int Count { get; }
}

public class InMemoryQueue<T> : IQueue<T>
{
    private readonly ConcurrentQueue<T> _queue = new();

    public void Enqueue(T item) => _queue.Enqueue(item);
    public bool TryDequeue(out T item) => _queue.TryDequeue(out item);
    public int Count => _queue.Count;
}

// -------------------------
// Retry Strategies
// -------------------------

public interface IRetryStrategy
{
    bool ShouldRetry(int retryCount);
    int GetNextDelayMs(int retryCount);
}

public class ExponentialBackoffRetryStrategy : IRetryStrategy
{
    private readonly RetrySetting _settings;

    public ExponentialBackoffRetryStrategy(RetrySetting settings)
    {
        _settings = settings;
    }

    public bool ShouldRetry(int retryCount) => retryCount < _settings.MaxRetryCount;

    public int GetNextDelayMs(int retryCount) =>
        _settings.RetryDelayMs * (int)Math.Pow(2, retryCount);
}

public class FixedWindowRetryStrategy : IRetryStrategy
{
    private readonly RetrySetting _settings;

    public FixedWindowRetryStrategy(RetrySetting settings)
    {
        _settings = settings;
    }

    public bool ShouldRetry(int retryCount) => retryCount < _settings.MaxRetryCount;

    public int GetNextDelayMs(int retryCount) => _settings.RetryDelayMs;
}

// -------------------------
// Providers (Strategy)
// -------------------------

public interface INotificationProvider
{
    ChannelType Channel { get; }
    bool Send(NotificationDelivery delivery, Notification notification);
}

public class EmailProvider : INotificationProvider
{
    public ChannelType Channel => ChannelType.Email;

    public bool Send(NotificationDelivery delivery, Notification notification)
    {
        Console.WriteLine($"[Email] Sending '{notification.Subject}' to user {notification.UserId}");
        // plug in SES / SendGrid here
        return true;
    }
}

public class SmsProvider : INotificationProvider
{
    public ChannelType Channel => ChannelType.Sms;

    public bool Send(NotificationDelivery delivery, Notification notification)
    {
        Console.WriteLine($"[SMS] Sending '{notification.Body}' to user {notification.UserId}");
        // plug in Twilio here
        return true;
    }
}

public class PushProvider : INotificationProvider
{
    public ChannelType Channel => ChannelType.Push;

    public bool Send(NotificationDelivery delivery, Notification notification)
    {
        Console.WriteLine($"[Push] Sending '{notification.Subject}' to user {notification.UserId}");
        // plug in FCM here
        return true;
    }
}

// -------------------------
// Provider Factory
// -------------------------

public interface INotificationProviderFactory
{
    INotificationProvider GetProvider(ChannelType channel);
}

public class NotificationProviderFactory : INotificationProviderFactory
{
    private readonly Dictionary<ChannelType, INotificationProvider> _providers;

    public NotificationProviderFactory(IEnumerable<INotificationProvider> providers)
    {
        _providers = providers.ToDictionary(p => p.Channel);
    }

    public INotificationProvider GetProvider(ChannelType channel)
    {
        if (!_providers.TryGetValue(channel, out var provider))
            throw new InvalidOperationException($"No provider registered for {channel}");

        return provider;
    }
}

// -------------------------
// Repositories
// -------------------------

public interface INotificationRepository
{
    Guid AddNotification(Notification notification);
    bool AddNotificationDelivery(NotificationDelivery delivery);   // returns false if duplicate
    void UpdateNotificationDelivery(NotificationDelivery delivery);
}

public class InMemoryNotificationRepository : INotificationRepository
{
    private readonly ConcurrentDictionary<Guid, Notification> _notifications = new();
    private readonly ConcurrentDictionary<Guid, NotificationDelivery> _deliveries = new();

    // key = "notificationId:channel" — enforces idempotency
    private readonly ConcurrentDictionary<string, bool> _idempotencyKeys = new();

    public Guid AddNotification(Notification notification)
    {
        _notifications.TryAdd(notification.Id, notification);
        return notification.Id;
    }

    public bool AddNotificationDelivery(NotificationDelivery delivery)
    {
        var key = $"{delivery.NotificationId}:{delivery.Channel}";

        if (!_idempotencyKeys.TryAdd(key, true))
        {
            Console.WriteLine($"[Idempotency] Duplicate delivery blocked for {key}");
            return false;
        }

        _deliveries.TryAdd(delivery.Id, delivery);
        return true;
    }

    public void UpdateNotificationDelivery(NotificationDelivery delivery)
    {
        _deliveries[delivery.Id] = delivery;
    }
}

public interface IUserRepository
{
    List<ChannelType> GetUserPreferences(Guid userId);
}

public class InMemoryUserRepository : IUserRepository
{
    private readonly Dictionary<Guid, User> _users;

    public InMemoryUserRepository()
    {
        var userId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        _users = new Dictionary<Guid, User>
        {
            [userId] = new User
            {
                Id = userId,
                Name = "Mayank",
                Preferences = new List<ChannelType> { ChannelType.Email, ChannelType.Push }
            }
        };
    }

    public List<ChannelType> GetUserPreferences(Guid userId)
    {
        return _users.TryGetValue(userId, out var user)
            ? user.Preferences
            : new List<ChannelType>();
    }
}

// -------------------------
// Worker
// -------------------------

public interface IWorker
{
    void Start(CancellationToken ct);
}

public class NotificationWorker : IWorker
{
    private readonly ChannelType _channel;
    private readonly IQueue<NotificationJob> _mainQueue;
    private readonly IQueue<NotificationJob> _retryQueue;
    private readonly IQueue<NotificationJob> _dlqQueue;
    private readonly INotificationProviderFactory _providerFactory;
    private readonly IRetryStrategy _retryStrategy;
    private readonly INotificationRepository _repository;

    public NotificationWorker(
        ChannelType channel,
        IQueue<NotificationJob> mainQueue,
        IQueue<NotificationJob> retryQueue,
        IQueue<NotificationJob> dlqQueue,
        INotificationProviderFactory providerFactory,
        IRetryStrategy retryStrategy,
        INotificationRepository repository)
    {
        _channel = channel;
        _mainQueue = mainQueue;
        _retryQueue = retryQueue;
        _dlqQueue = dlqQueue;
        _providerFactory = providerFactory;
        _retryStrategy = retryStrategy;
        _repository = repository;
    }

    public void Start(CancellationToken ct)
    {
        // main queue consumer
        Task.Run(() => ConsumeQueue(_mainQueue, ct), ct);

        // retry queue consumer — runs the same logic
        Task.Run(() => ConsumeQueue(_retryQueue, ct), ct);
    }

    private async Task ConsumeQueue(IQueue<NotificationJob> queue, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (queue.TryDequeue(out var job))
            {
                await ProcessJob(job);
            }
            else
            {
                await Task.Delay(100, ct); // avoid busy-waiting when queue is empty
            }
        }
    }

    private async Task ProcessJob(NotificationJob job)
    {
        var delivery = job.Delivery;
        var provider = _providerFactory.GetProvider(_channel);

        try
        {
            delivery.LastTriedAt = DateTime.UtcNow;
            var success = provider.Send(delivery, job.Notification);

            if (success)
            {
                delivery.Status = DeliveryStatus.Sent;
                _repository.UpdateNotificationDelivery(delivery);
                Console.WriteLine($"[{_channel}] Delivered notification {delivery.NotificationId}");
            }
            else
            {
                await HandleFailure(job);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{_channel}] Exception: {ex.Message}");
            await HandleFailure(job);
        }
    }

    private async Task HandleFailure(NotificationJob job)
    {
        var delivery = job.Delivery;
        delivery.RetryCount++;

        if (_retryStrategy.ShouldRetry(delivery.RetryCount))
        {
            var delay = _retryStrategy.GetNextDelayMs(delivery.RetryCount);
            delivery.Status = DeliveryStatus.Retrying;
            _repository.UpdateNotificationDelivery(delivery);

            Console.WriteLine($"[{_channel}] Retry #{delivery.RetryCount} in {delay}ms for {delivery.NotificationId}");

            await Task.Delay(delay); // in production: re-enqueue with visibility delay, not sleep
            _retryQueue.Enqueue(job);
        }
        else
        {
            delivery.Status = DeliveryStatus.Failed;
            _repository.UpdateNotificationDelivery(delivery);

            Console.WriteLine($"[{_channel}] Max retries hit. Moving to DLQ: {delivery.NotificationId}");
            _dlqQueue.Enqueue(job);
        }
    }
}

// -------------------------
// Notification Service
// -------------------------

public interface INotificationService
{
    Guid SendNotification(Notification notification);
}

public class NotificationService : INotificationService
{
    private readonly INotificationRepository _notificationRepo;
    private readonly IUserRepository _userRepo;

    // one set of queues per channel — created here, shared with workers
    private readonly Dictionary<ChannelType, IQueue<NotificationJob>> _mainQueues;
    private readonly Dictionary<ChannelType, IQueue<NotificationJob>> _retryQueues;
    private readonly Dictionary<ChannelType, IQueue<NotificationJob>> _dlqQueues;

    private readonly List<IWorker> _workers = new();
    private readonly CancellationTokenSource _cts = new();

    public NotificationService(
        INotificationRepository notificationRepo,
        IUserRepository userRepo,
        INotificationProviderFactory providerFactory,
        RetrySetting retrySetting)
    {
        _notificationRepo = notificationRepo;
        _userRepo = userRepo;

        _mainQueues = new();
        _retryQueues = new();
        _dlqQueues = new();

        // bootstrap one worker per channel — same queue refs passed to both service and worker
        foreach (ChannelType channel in Enum.GetValues(typeof(ChannelType)))
        {
            var mainQueue = new InMemoryQueue<NotificationJob>();
            var retryQueue = new InMemoryQueue<NotificationJob>();
            var dlqQueue = new InMemoryQueue<NotificationJob>();

            _mainQueues[channel] = mainQueue;
            _retryQueues[channel] = retryQueue;
            _dlqQueues[channel] = dlqQueue;

            IRetryStrategy strategy = retrySetting.RetryType == RetryType.ExponentialBackoff
                ? new ExponentialBackoffRetryStrategy(retrySetting)
                : new FixedWindowRetryStrategy(retrySetting);

            var worker = new NotificationWorker(
                channel,
                mainQueue,
                retryQueue,
                dlqQueue,
                providerFactory,
                strategy,
                notificationRepo
            );

            _workers.Add(worker);
            worker.Start(_cts.Token);
        }
    }

    public Guid SendNotification(Notification notification)
    {
        // if caller didn't specify channels, fall back to user preferences
        if (!notification.Channels.Any())
            notification.Channels = _userRepo.GetUserPreferences(notification.UserId);

        if (!notification.Channels.Any())
        {
            Console.WriteLine($"[Service] No channels found for user {notification.UserId}. Skipping.");
            return notification.Id;
        }

        // persist the notification first
        _notificationRepo.AddNotification(notification);

        // fan out to per-channel queues
        foreach (var channel in notification.Channels)
        {
            var delivery = new NotificationDelivery
            {
                NotificationId = notification.Id,
                Channel = channel,
                Status = DeliveryStatus.Enqueued
            };

            // idempotency check — TryAdd returns false on duplicate
            var added = _notificationRepo.AddNotificationDelivery(delivery);
            if (!added) continue;

            // enqueue — if this fails, recovery job (not shown) re-enqueues
            // based on Enqueued status records stuck longer than threshold
            _mainQueues[channel].Enqueue(new NotificationJob
            {
                Notification = notification,
                Delivery = delivery
            });
        }

        Console.WriteLine($"[Service] Accepted notification {notification.Id} for channels: {string.Join(", ", notification.Channels)}");
        return notification.Id;
    }

    public void Shutdown() => _cts.Cancel();
}

// -------------------------
// Entry Point
// -------------------------

class NotificationSystem
{
    public async Task SendNotifications()
    {
        var retrySetting = new RetrySetting
        {
            MaxRetryCount = 3,
            RetryDelayMs = 200,
            RetryType = RetryType.ExponentialBackoff
        };

        var providers = new List<INotificationProvider>
        {
            new EmailProvider(),
            new SmsProvider(),
            new PushProvider()
        };

        var providerFactory = new NotificationProviderFactory(providers);
        var notificationRepo = new InMemoryNotificationRepository();
        var userRepo = new InMemoryUserRepository();

        var service = new NotificationService(notificationRepo, userRepo, providerFactory, retrySetting);

        var userId = Guid.Parse("00000000-0000-0000-0000-000000000001");

        // test 1: explicit channels
        service.SendNotification(new Notification
        {
            UserId = userId,
            Subject = "Order Shipped",
            Body = "Your order #1234 has shipped.",
            Channels = new List<ChannelType> { ChannelType.Email, ChannelType.Sms }
        });

        // test 2: no channels — falls back to user preferences (Email + Push)
        service.SendNotification(new Notification
        {
            UserId = userId,
            Subject = "Promo Alert",
            Body = "50% off today only!"
        });

        // test 3: duplicate — same notification id, same channel — idempotency should block it
        var dupNotification = new Notification
        {
            Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            UserId = userId,
            Subject = "Duplicate Test",
            Body = "Should only send once.",
            Channels = new List<ChannelType> { ChannelType.Email }
        };
        service.SendNotification(dupNotification);
        service.SendNotification(dupNotification); // second call — should be blocked

        // give workers time to process
        await Task.Delay(2000);

        service.Shutdown();
        Console.WriteLine("\nDone.");
    }
}