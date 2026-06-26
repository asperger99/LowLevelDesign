# LRU Cache -- Pre-Coding Design Draft

## Requirements

### Functional
- `Get(key)` -- return value if present, indicate miss if not
- `Put(key, value)` -- insert or update, evict LRU entry if at capacity
- `Delete(key)` -- remove entry by key
- Eviction policy: Least Recently Used
- TTL: absolute expiry (N seconds after insert), stretch goal

### Non-Functional
- Multi-threaded, in-process cache
- Read-heavy workload (~80% reads, 20% writes)
- Capacity configured at construction time (up to 10,000 entries)
- All operations O(1)
- No persistence -- evicted entries are gone, cache miss triggers caller to fetch from source
- High throughput on reads -- reads must not block each other

---

## Concurrency Strategy
- Reads (`TryGet`) must not block -- use `ConcurrentDictionary` for lock-free O(1) lookup
- Every `Get` promotes a key (moves to head of linked list) -- this is a write on the ordering structure
- Synchronous promotion would require a write lock on every read -- unacceptable for read-heavy workload
- **Decision: Async Promotion (near-LRU)**
  - `TryGet` enqueues a promotion event and returns immediately
  - Single background `PromotionWorker` drains queue in batches and mutates linked list
  - Single worker means linked list needs no lock
  - Tradeoff: ~10ms inconsistency window, may make suboptimal eviction under pressure -- acceptable

---

## Enums

```csharp
public enum EventType
{
    Insert,
    Access,
    Delete
}
```

---

## Entities

### `EvictionResult<TKey>`
- Purpose: return value from `Put`, tells caller if an eviction occurred and which key was evicted
- Fields: `bool WasEvicted`, `TKey? EvictedKey`
- Immutable: use `init` accessors

### `CacheNode<TValue>` (TTL extension only)
- Purpose: wraps value with expiry metadata in the dictionary
- Fields: `TValue Value`, `DateTime ExpiresAt`
- Stored only in `ConcurrentDictionary` -- linked list stays as `LinkedList<TKey>`

---

## Interfaces

### `ICache<TKey, TValue>` -- public client-facing contract
```
TryGet(TKey key, out TValue value) → bool
Put(TKey key, TValue value) → EvictionResult<TKey>
Delete(TKey key) → bool
```

### `ICacheStrategy<TKey>` -- eviction strategy abstraction
```
OnInsert(TKey key) → void
OnAccess(TKey key) → void
OnDelete(TKey key) → void
TryGetEvictionCandidate(out TKey key) → bool
```

---

## Classes

### `CacheManager<TKey, TValue>`
- Implements: `ICache<TKey, TValue>`, `IDisposable`
- Responsibility: public entry point, orchestrates dictionary, strategy, and worker
- Owns:
  - `ConcurrentDictionary<TKey, TValue>` -- primary data store
  - `ConcurrentQueue<(TKey, EventType)>` -- async promotion event buffer
  - `ICacheStrategy<TKey>` -- injected eviction strategy
  - `PromotionWorker<TKey>` -- background worker
  - `CancellationTokenSource` -- worker lifecycle
  - `int Capacity`
- Wires up worker on construction, cancels on Dispose

### `LruCacheStrategy<TKey>`
- Implements: `ICacheStrategy<TKey>`
- Responsibility: maintains recency ordering, nominates eviction candidate
- Owns:
  - `LinkedList<TKey>` -- recency order, head = most recent, tail = eviction candidate
  - `Dictionary<TKey, LinkedListNode<TKey>>` -- O(1) node access by key
- Note: only ever touched by single `PromotionWorker` thread -- no locks needed

### `PromotionWorker<TKey>`
- Responsibility: background consumer, drains promotion queue in batches, calls strategy methods
- Owns: reference to `ICacheStrategy`, `ConcurrentQueue`, `CancellationToken`
- Batch size: configurable (default 5)
- Yields between batches via `Task.Delay(10ms)`
- Exceptions inside event processing are logged and suppressed -- worker never dies on strategy error

### `ExpiryWorker<TKey>` (TTL extension)
- Responsibility: background sweep, removes TTL-expired entries
- Owned by: `CacheManager`
- Uses `PriorityQueue<TKey, DateTime>` min-heap -- peeks head, stops sweep as soon as head is not expired
- Enqueues `EventType.Delete` for expired keys

---

## Design Patterns

### Strategy Pattern
- `ICacheStrategy<TKey>` abstracts eviction policy
- `CacheManager` is strategy-agnostic -- inject `LruCacheStrategy` today, `LfuCacheStrategy` tomorrow
- Justification: eviction logic is the only axis of variation between cache types. Isolating it means zero changes to `CacheManager` when swapping policy.

### Producer-Consumer Pattern
- `CacheManager` produces `(TKey, EventType)` tuples onto `ConcurrentQueue`
- `PromotionWorker` consumes independently on its own cadence
- Decouples read throughput from ordering maintenance

---

## Key Design Decisions

| Decision | Choice | Reason |
|---|---|---|
| Promotion timing | Async | Synchronous promotion requires write lock on every read |
| Worker count | Single | Multiple workers still need a lock on linked list |
| Strategy ownership of linked list | Yes | Clean boundary, `CacheManager` stays strategy-agnostic |
| `CacheNode` in linked list | No | Would couple linked list structure to LRU-specific data |
| TTL type | Absolute | Simpler, no need to update timestamp on every access |
| Eviction on full cache, no candidate | Reject insert | Safer than undefined behavior |

---

## Wiring (Construction)

```
CacheManager(strategy, capacity)
  └── creates ConcurrentDictionary
  └── creates ConcurrentQueue
  └── creates CancellationTokenSource
  └── creates PromotionWorker(strategy, queue, cts.Token)
  └── calls worker.Start()
```

---

## Disposal Chain

```
CacheManager.Dispose()
  └── _cts.Cancel()       -- signals PromotionWorker to stop
  └── _cts.Dispose()      -- releases OS WaitHandle
  └── strategy.Dispose()  -- if IDisposable, propagates down
```
