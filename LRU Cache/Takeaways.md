# LRU Cache LLD -- Takeaways

## Requirements Clarification
- Never jump to design without asking about: data shape, capacity, concurrency model, read/write ratio, consistency requirements, TTL, and persistence.
- At staff level you are expected to drive this phase entirely without prompting.
- Checklist to run through every time:
  - What data is being stored?
  - What is the max capacity?
  - Single-threaded or multi-threaded?
  - Read-heavy or write-heavy?
  - Do we need TTL?
  - What happens on cache miss -- silent or throw?
  - Do we need persistence or durability on eviction?

---

## Concurrency

### Every Get is a write
- In an LRU cache, `Get` is not a pure read. It moves the accessed node to the head of the linked list.
- `ReaderWriterLockSlim` buys nothing here because every Get needs a write lock on the ordering structure.

### Async Promotion Pattern
- Decouple the read path from the ordering update entirely.
- `Get` reads from `ConcurrentDictionary` (lock-free) and returns immediately.
- The "move to head" promotion is enqueued onto a `ConcurrentQueue`.
- A single background `PromotionWorker` drains the queue in batches and mutates the linked list.
- Since only one thread ever touches the linked list, it needs no lock at all.
- This is called **near-LRU** not strict-LRU. The inconsistency window is bounded by the worker's drain interval (~10ms).
- Production caches like **Caffeine (Java)** use this exact approach and document it explicitly as a design decision.

### Multiple workers don't help
- Multiple workers draining the same queue still need a lock on the linked list.
- The correct solution is a **single worker thread** -- no lock needed, simpler, faster.

---

## Data Structures
- `ConcurrentDictionary<TKey, TValue>` for O(1) thread-safe key lookup.
- `LinkedList<TKey>` for O(1) eviction candidate (tail) and O(1) promotion (head).
- `Dictionary<TKey, LinkedListNode<TKey>>` inside the strategy for O(1) node access by key -- avoids O(n) list traversal on promotion.
- `ConcurrentQueue` for the async promotion event buffer.
- `PriorityQueue` (min-heap by expiry time) for efficient TTL expiry sweeps.

---

## Design Patterns

### Strategy Pattern -- `ICacheStrategy<TKey>`
- Eviction policy is swappable at construction time via dependency injection.
- Today: `LruCacheStrategy`. Tomorrow: `LfuCacheStrategy`, `ARCStrategy`, etc.
- `CacheManager` never knows which strategy is active.
- Justified because eviction logic is the only thing that differs between cache types.

### Producer-Consumer Pattern -- `PromotionWorker`
- `CacheManager` produces promotion events onto `ConcurrentQueue`.
- `PromotionWorker` consumes them independently on its own cadence.
- Do not confuse this with the Observer pattern. Observer is one-to-many event broadcasting. This is a single background consumer loop.

---

## API Design
- Use generics `ICache<TKey, TValue>` -- avoids boxing and unsafe casts.
- Use `TryGet(TKey key, out TValue value)` -- idiomatic .NET pattern, same as `Dictionary.TryGetValue`. Distinguishes cache miss from cached null.
- `Put` returns `EvictionResult<TKey>` -- caller may need to know what was evicted for downstream invalidation.
- `Delete` returns `bool` -- true if key existed and was removed.
- Collapse `Write` and `Update` into a single `Put` (upsert semantics).

---

## Clean Boundaries
- `CacheManager` stores raw `TValue` in the dictionary -- no node references.
- `LruCacheStrategy` owns its internal node structure entirely (`LinkedList`, `Dictionary<TKey, LinkedListNode<TKey>>`).
- `CacheManager` and strategy communicate only through keys, not nodes.
- This means swapping strategy never requires changes to `CacheManager`.

---

## TTL Design
- Support **absolute TTL** -- expires N seconds after insert regardless of access.
- Lazy expiration: check `ExpiresAt` on `TryGet`, delete if expired.
- Problem: entries never accessed again after expiry sit in cache forever consuming capacity.
- Solution: `ExpiryWorker` background sweep using a `PriorityQueue<TKey, DateTime>` min-heap ordered by expiry time.
- Sweep peeks at head -- if not expired, nothing else is either. O(log n) vs O(n) scan.
- `CacheNode<TValue>` with `Value` and `ExpiresAt` stored only in the dictionary. Linked list stays as `LinkedList<TKey>`, untouched.

---

## .NET Fundamentals

### `await Task.Delay(10, cancellationToken)` -- yielding between batches
- Without delay, an empty queue causes a busy wait -- thread spins at 100% CPU doing nothing.
- `Task.Delay` releases the thread back to the thread pool and wakes it after the interval.
- Called cooperative yielding. Tradeoff: up to 10ms latency before a promotion event is processed.

### `CancellationToken` -- cooperative cancellation
- Owner creates `CancellationTokenSource`, passes `Token` to workers.
- Worker checks `IsCancellationRequested` in its loop condition.
- `_cts.Cancel()` flips the flag -- worker exits cleanly on next iteration.
- Passing the token to `Task.Delay` interrupts the delay immediately on cancellation rather than waiting the full interval.
- Always call `_cts.Dispose()` -- it holds an OS-level `WaitHandle` that must be released.
- Do not use a plain `bool` flag -- requires `volatile` keyword to be safe across threads.

### `IDisposable` -- deterministic cleanup
- GC handles memory but not OS handles, background threads, or network connections.
- `IDisposable` gives explicit control over when these are released.
- `using var cache = new CacheManager(...)` -- compiler generates try/finally that calls `Dispose` automatically.
- Each owner is responsible for disposing what it owns. Propagate disposal down the ownership chain.
- Without Dispose: `PromotionWorker` keeps running after `CacheManager` goes out of scope. Memory and thread leak.

---

## C# Hygiene to Drill
- PascalCase for all public methods and properties.
- `readonly` on fields that are set once in the constructor.
- `init` accessors on immutable return value objects (introduced C# 9 / .NET 5, not .NET 9).
- `_keyNodeMap[key] = node` preferred over `TryAdd` on plain `Dictionary` for clarity.
- `async Task` not `async void` for background methods -- exceptions in `async void` are unobservable and crash the process.

---

## Interview Delivery Tips
- When discussing near-LRU tradeoff: quantify the inconsistency window, explain the worst case is suboptimal eviction not data corruption, reference Caffeine as validation.
- Only name a design pattern when it genuinely fits. Saying "observer pattern" for a background consumer loop is incorrect and will be caught.
- At staff level: make deliberate choices and articulate the tradeoff, not just the clever solution.
