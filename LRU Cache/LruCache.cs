using System.Collections.Concurrent;
using System.Globalization;

namespace LowLevelDesign.LRU_Cache;
// Enum
public enum EventType
{
    Insert,
    Access,
    Delete
}
// entities
public class EvictionResult<TKey>
{
    public bool wasEvicted { get; init; }
    public TKey? evictedkey { get; init; }
    
    // init is an accessor introduced in C# 9 that allows a property to be set only during object initialization,
    // making it effectively immutable afterward.
}

//Interfaces
public interface ICache<TKey, TValue>
{
    bool TryGet(TKey key, out TValue value);
    EvictionResult<TKey> Put(TKey key, TValue value);
    bool Delete(TKey key);
}

public interface ICacheStrategy<TKey>
{

    void OnInsert(TKey key);
    void OnAccess(TKey key);
    void OnDelete(TKey key);
    bool TryGetEvictionCandidate(out TKey key);
}

// Strategy

public class LruCacheStrategy<TKey> : ICacheStrategy<TKey>
{
    private Dictionary<TKey, LinkedListNode<TKey>> _keyNodeMap = new();
    private LinkedList<TKey> _lruList = new();
    public void OnInsert(TKey key)
    {
        if (_keyNodeMap.ContainsKey(key))
        {
            OnAccess(key);
            return;
        }
        var linkedListNode = _lruList.AddFirst(key);
        _keyNodeMap.TryAdd(key, linkedListNode);
    }

    public void OnAccess(TKey key) // read, update
    {
        if(_keyNodeMap.TryGetValue(key, out var node))
        {
            _lruList.Remove(node);
            _lruList.AddFirst(node);
        }
    }

    public void OnDelete(TKey key)
    {
        if (_keyNodeMap.TryGetValue(key, out var node))
        {
            _lruList.Remove(node);
            _keyNodeMap.Remove(key);
        }
    }

    public bool TryGetEvictionCandidate(out TKey key)
    {
        if (_lruList.Count > 0)
        {
            key = _lruList.Last.Value;
            return true;
        }
        key = default;
        return false;
    }
}

// Background job
public class PromotionWorker<TKey>
{
    private ICacheStrategy<TKey> _cacheStrategy;
    private ConcurrentQueue<(TKey key, EventType eventType)> _queue;
    private CancellationToken _cancellationToken;
    private int _batchSize = 5;
    public PromotionWorker(ICacheStrategy<TKey> cacheStrategy, ConcurrentQueue<(TKey key, EventType eventType)> queue, CancellationToken cancellationToken)
    {
        _cacheStrategy = cacheStrategy;
        _queue = queue;
        _cancellationToken = cancellationToken;
    }

    public void Start()
    {
        Task.Run(Process, _cancellationToken);
    }

    private async Task Process()
    {
        while (!_cancellationToken.IsCancellationRequested)
        {
            int processed = 0;
            while (_queue.Count > 0 && processed < _batchSize)
            {
                try
                {
                    if (_queue.TryDequeue(out (TKey key, EventType eventType) item))
                    {
                        switch (item.eventType)
                        {
                            case EventType.Insert:
                                _cacheStrategy.OnInsert(item.key);
                                break;
                            case EventType.Access:
                                _cacheStrategy.OnAccess(item.key);
                                break;
                            case EventType.Delete:
                                _cacheStrategy.OnDelete(item.key);
                                break;
                            default:
                                Console.WriteLine("Invalid Event observed inside EvictionWorker " + item.eventType);
                                break;
                        }

                        processed++;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Exception inside promotion worker : {ex.Message}");
                }
                
            }
            // yield between batches
            await Task.Delay(10, _cancellationToken);
        }
    }
}

public class CacheManager<TKey, TValue>: ICache<TKey, TValue>, IDisposable
{
    private ICacheStrategy<TKey> _cacheStrategy;
    private int _capacity;
    private ConcurrentDictionary<TKey, TValue> _cache = new();
    private readonly ConcurrentQueue<(TKey key, EventType eventType)> _queue=new();
    private readonly PromotionWorker<TKey> _promotionWorker;
    private readonly CancellationTokenSource _cts = new();
    public CacheManager(ICacheStrategy<TKey> cacheStrategy, int capacity)
    {
        _cacheStrategy = cacheStrategy;
        _capacity = capacity;
        _promotionWorker = new PromotionWorker<TKey>(_cacheStrategy, _queue, _cts.Token);
        _promotionWorker.Start();
    }
    public bool TryGet(TKey key, out TValue value)
    {
        if (_cache.TryGetValue(key, out value))
        {
            _queue.Enqueue((key, EventType.Access));
            return true;
        }
        return false;
    }

    public EvictionResult<TKey> Put(TKey key, TValue value)
    {
        if (_cache.TryGetValue(key, out TValue currentValue))
        {
            _cache.TryUpdate(key, value, currentValue);
            _queue.Enqueue((key, EventType.Access));
            return new EvictionResult<TKey>()
            {
                wasEvicted = false
            };
        }

        if (_cache.Count >= _capacity)
        {
            if (_cacheStrategy.TryGetEvictionCandidate(out TKey cacheEvictedKey))
            {
                _cache.TryRemove(cacheEvictedKey, out _);
                _queue.Enqueue((cacheEvictedKey, EventType.Delete));
                _cache.TryAdd(key, value);
                _queue.Enqueue((key, EventType.Insert));
                return new EvictionResult<TKey> { wasEvicted = true, evictedkey = cacheEvictedKey};
            }
            else
            {
                Console.WriteLine("Cache full, Eviction failed");
                return new EvictionResult<TKey> { wasEvicted = false};
            }
        }
        _cache.TryAdd(key, value);
        _queue.Enqueue((key, EventType.Insert));
        return new EvictionResult<TKey> { wasEvicted = false };
    }

    public bool Delete(TKey key)
    {
        if (_cache.TryRemove(key, out TValue value))
        {
            _queue.Enqueue((key, EventType.Delete));
            return true;
        }
        return false;
    }
    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        if (_cacheStrategy is IDisposable disposable)
            disposable.Dispose();
    }
}

public class LruCache
{
    
    
}