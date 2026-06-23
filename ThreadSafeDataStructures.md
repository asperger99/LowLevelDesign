# Thread-Safe Data Structures in .NET

## Why They Matter

In multi-threaded code, plain collections like `List<T>` or `Dictionary<K,V>` are **not thread-safe** — concurrent reads and writes cause race conditions, torn state, or exceptions. .NET provides two layers of solutions:

1. **`System.Collections.Concurrent`** — lock-free or fine-grained-lock collections for high-throughput scenarios
2. **Explicit locking** (`lock`, `Monitor`, `SemaphoreSlim`) around plain collections when you need custom atomicity

---

## `System.Collections.Concurrent` — The Go-To Namespace

### `ConcurrentQueue<T>`

A FIFO queue where `TryDequeue` is **atomic** — the dequeue itself is the synchronization point.

```csharp
var queue = new ConcurrentQueue<ParkingSpot>();
queue.Enqueue(spot);

if (queue.TryDequeue(out var spot))
    // guaranteed: only ONE thread got this spot
```

**Used in this project:** `AvailableParkingSlot` strategy holds three `ConcurrentQueue<ParkingSpot>` (one per vehicle type). Two threads calling `GetParkingSpot()` concurrently can never receive the same spot — the atomic dequeue IS the race-condition fix. No separate lock needed.

**When to use:** Producer/consumer pipelines, work queues, any pattern where "claim one item" must be atomic.

---

### `ConcurrentDictionary<TKey, TValue>`

A dictionary safe for concurrent reads and writes. Key operations are atomic.

```csharp
var dict = new ConcurrentDictionary<int, Floor>();

// Atomic add — returns false if key already exists
dict.TryAdd(1, new Floor(1));

// Atomic read
dict.TryGetValue(1, out var floor);

// Atomic conditional update — only updates if current value matches
dict.TryUpdate(key, newValue, expectedCurrentValue);

// Add or return existing — safe shortcut for GetOrAdd patterns
var lockObj = dict.GetOrAdd(spotId, _ => new object());
```

**Used in this project:**
- `ParkingFloorRepository._floors` — concurrent floor lookups and additions
- `TicketRepository._tickets` — concurrent ticket storage
- `ParkingFloorService._spotLocks` — per-spot lock objects fetched with `GetOrAdd`

**When to use:** Shared lookup tables, caches, registries accessed by multiple threads.

> **Gotcha:** `GetOrAdd` is NOT atomic as a whole — the factory can be called multiple times under contention. If the factory has side effects, use `TryAdd` in a loop or a `Lazy<T>` value instead.

---

### `ConcurrentStack<T>`

LIFO stack. `TryPop` is atomic. Less commonly needed than `ConcurrentQueue`.

```csharp
var stack = new ConcurrentStack<int>();
stack.Push(1);
stack.TryPop(out var item);
stack.TryPopRange(buffer, 0, 5); // pop up to 5 items at once
```

**When to use:** Undo stacks, DFS work queues, LIFO scheduling.

---

### `ConcurrentBag<T>`

An unordered collection where **each thread maintains its own local list** internally. When a thread adds and removes its own items, there is zero contention — threads never touch each other's lists. Order is not guaranteed.

The classic use case is an **object pool** — reusable objects that are expensive to create (DB connections, HTTP clients, parsers). Instead of creating a new one every time and throwing it away, threads return objects back to the pool when done and grab one next time.

Imagine a **car wash** with a rack of chamois cloths. Each worker grabs a cloth, uses it, then puts it back. Workers mostly pick up and return their own cloth — they rarely need to grab one from a colleague's pile.

```csharp
public class DatabaseConnectionPool
{
    private readonly ConcurrentBag<DbConnection> _pool = new();

    // Called when a thread needs a connection
    public DbConnection Rent()
    {
        // Try to reuse an existing connection from the pool
        if (_pool.TryTake(out var connection))
            return connection;

        // Pool is empty — create a new one (expensive, but rare)
        return CreateNewConnection();
    }

    // Called when a thread is done with its connection
    public void Return(DbConnection connection)
    {
        _pool.Add(connection); // put it back for reuse
    }
}
```

A typical usage pattern across threads:

```csharp
var conn = pool.Rent();
try
{
    await conn.QueryAsync("SELECT ...");
}
finally
{
    pool.Return(conn); // always return, even on exception
}
```

Each thread rents a connection, does its work, and returns it. The same thread that rented it usually returns it — this is exactly what `ConcurrentBag` is optimised for. No order needed, just "grab any available one."

**vs `ConcurrentQueue`:** Queue preserves order (FIFO) and is designed for producer/consumer where different threads add and remove. `ConcurrentBag` drops order entirely in exchange for near-zero contention when threads mostly interact with their own items.

> **Gotcha:** If threads frequently take items added by *other* threads, `ConcurrentBag` has to steal from another thread's local list — this adds overhead. In that case, prefer `ConcurrentQueue`.

**When to use:** Object pools (DB connections, HTTP clients, buffers) where threads rent and return their own objects.

---

### `BlockingCollection<T>`

A **bounded, blocking** wrapper around any `IProducerConsumerCollection<T>` (defaults to `ConcurrentQueue`). Blocks the consumer when empty and the producer when full.

```csharp
var channel = new BlockingCollection<WorkItem>(boundedCapacity: 100);

// Producer thread
channel.Add(new WorkItem());
channel.CompleteAdding(); // signal no more items

// Consumer thread — blocks until item available or completed
foreach (var item in channel.GetConsumingEnumerable())
    Process(item);
```

**When to use:** Classic producer/consumer pipelines with backpressure. For modern code, prefer `System.Threading.Channels` instead (more composable, async-friendly).

---

## Explicit Locking — When Concurrent Collections Aren't Enough

Use explicit locks when you need **multi-step atomicity** that a single collection operation can't give you (e.g. read-then-write as one atomic unit).

### `lock` / `Monitor`

Imagine a **ticket counter** with one clerk. Only one customer can be served at a time — the next one waits outside until the current one is done.

The problem `lock` solves is **multi-step operations that must appear as one**. Without it:

```
Thread A:  checks _seats > 0      ← true, 1 seat left
Thread B:  checks _seats > 0      ← also true, same seat!
Thread A:  books the seat
Thread B:  books the seat          ← double booking
```

With `lock`, Thread B waits at the door while Thread A does both steps:

```csharp
public class SeatBooking
{
    private int _availableSeats = 10;
    private readonly object _lock = new();

    public bool BookSeat(string customerName)
    {
        lock (_lock) // only one thread inside at a time
        {
            if (_availableSeats == 0)
                return false;

            _availableSeats--;  // check + decrement are now one atomic unit
            Console.WriteLine($"{customerName} booked a seat. Remaining: {_availableSeats}");
            return true;
        }
    }
}
```

If 10 threads call `BookSeat` simultaneously, they queue up one by one — no two threads ever see the same seat count at the same time.

**Used in this project:** `Floor._spotsLock` guards `_parkingSpots` list mutations. `ParkingFloorService._spotLocks` (per-spot) wraps the re-check-then-commit sequence in `ParkVehicleAtSpot` and `FreeSpot` — this is the **defensive fallback** layer after the strategy's atomic dequeue.

**When to use:** Any time you need multiple operations to appear as one. Keep the critical section as small as possible.

---

### `ReaderWriterLockSlim`

Allows **many concurrent readers OR one exclusive writer**. Better than `lock` when reads vastly outnumber writes.

Imagine a **stock price board** in a trading office. Hundreds of traders glance at it every second (reads). Once in a while, a clerk updates a price (write). There's no reason to stop all traders from reading just because another trader is also reading — the danger is only when someone is *writing*.

`ReaderWriterLockSlim` encodes exactly that policy:
- **Multiple readers** can hold the lock simultaneously — they don't block each other.
- **A writer** waits for all current readers to finish, then gets exclusive access — no new readers admitted until the write is done.

```csharp
public class StockBoard
{
    private readonly Dictionary<string, decimal> _prices = new();
    private readonly ReaderWriterLockSlim _rwLock = new();

    // Called by hundreds of trader threads simultaneously — all allowed in at once
    public decimal GetPrice(string ticker)
    {
        _rwLock.EnterReadLock();
        try
        {
            return _prices[ticker];
        }
        finally
        {
            _rwLock.ExitReadLock(); // always release, even if an exception is thrown
        }
    }

    // Called rarely by the price-feed thread — needs exclusive access
    public void UpdatePrice(string ticker, decimal price)
    {
        _rwLock.EnterWriteLock();
        try
        {
            _prices[ticker] = price; // no readers allowed in while this runs
        }
        finally
        {
            _rwLock.ExitWriteLock();
        }
    }
}
```

The `finally` block is critical — if you skip it and an exception is thrown inside, the lock is never released and every thread waiting on it freezes permanently.

**`EnterReadLock` doesn't block other readers — so why call it at all?**

It looks like no lock because readers don't block each other. But without it, this happens:

```
Writer:  starts updating _prices["AAPL"] = 182.50  ← halfway through
Reader:  reads _prices["AAPL"]                      ← sees torn/corrupt state
```

`EnterReadLock` says: "let me in only if no writer is currently active." If a writer holds the lock, the reader waits. The lock on the read path isn't to block other readers — it's to **coordinate with writers**.

| Who's inside | New reader wants in | New writer wants in |
|---|---|---|
| Nobody | ✅ enters immediately | ✅ enters immediately |
| One or more readers | ✅ enters immediately | ⏳ waits for all readers to leave |
| One writer | ⏳ waits | ⏳ waits |

Without `EnterReadLock`, readers don't register themselves anywhere — a writer has no way to know "are there active readers right now?" and can't wait for them to finish before writing.

**vs plain `lock`:** `lock` would force traders to queue up one at a time even just to *read* — unnecessarily strict. `ReaderWriterLockSlim` is the smarter rule: **reads in parallel are fine, only writes need the room to themselves.**

**When to use:** Config/cache objects read constantly but updated rarely. Not worth the complexity if write frequency is similar to read frequency.

---

### `SemaphoreSlim`

A counting semaphore — limits how many threads can enter a section simultaneously. Also has `async`-friendly `WaitAsync()`.

Imagine you have a payment gateway that can only handle **3 concurrent requests** — any more and it starts returning errors.

```csharp
public class PaymentGateway
{
    // Only 3 threads allowed inside at the same time
    private readonly SemaphoreSlim _sem = new(initialCount: 3);

    public async Task<bool> ChargeAsync(string ticketId, decimal amount)
    {
        await _sem.WaitAsync(); // blocks here if 3 others are already inside
        try
        {
            return await CallExternalGatewayAsync(ticketId, amount);
        }
        finally
        {
            _sem.Release(); // opens one slot — next waiting thread unblocks
        }
    }
}
```

If 10 threads call `ChargeAsync` simultaneously:
- Threads 1, 2, 3 enter immediately
- Threads 4–10 block at `WaitAsync()`
- As each of 1/2/3 finishes and calls `Release()`, the next waiting thread unblocks and enters

**vs `lock`:** `lock` allows only 1 thread at a time. `SemaphoreSlim` lets you allow **N threads** — you control the number.

**When to use:** Rate-limiting concurrent calls to a resource (DB connections, external APIs, file handles).

---

### `Interlocked`

Lock-free atomic operations on primitive types (`int`, `long`, `reference`). Cheaper than `lock` because they don't put threads to sleep — they use CPU-level atomic instructions directly.

The problem they solve: even `_counter++` is **not atomic** — it's three steps (read, increment, write). Two threads doing it simultaneously can both read the same value and write the same result, losing one increment.

```
Thread A: reads _counter = 5
Thread B: reads _counter = 5
Thread A: writes _counter = 6
Thread B: writes _counter = 6   ← increment lost, expected 7
```

Imagine a **ticket vending machine** that tracks how many tickets have been sold across multiple kiosks simultaneously:

```csharp
public class TicketCounter
{
    private int _totalSold = 0;

    public void SellTicket()
    {
        // NOT safe: _totalSold++ is read + increment + write — three steps
        // _totalSold++;

        // Safe: single atomic CPU instruction, no lock needed
        Interlocked.Increment(ref _totalSold);
    }

    public int GetTotal() => _totalSold;
}
```

Other operations:

```csharp
Interlocked.Add(ref _totalRevenue, ticketPrice);             // atomic +=

// CompareExchange — only update if current value matches expected
// "set to newVal only if it's still expectedVal right now"
Interlocked.CompareExchange(ref _status, newVal: 1, comparand: 0);
```

**vs `lock`:** `lock` can protect any number of statements. `Interlocked` only works on a **single variable** — but when that's all you need, it's significantly faster because no thread ever blocks.

**When to use:** Simple counters, flags, or CAS loops. Far cheaper than `lock` for single-variable updates.

---

## Choosing the Right Tool

| Scenario | Use |
|---|---|
| Claim one item atomically (queue) | `ConcurrentQueue<T>.TryDequeue` |
| Shared lookup / cache | `ConcurrentDictionary<K,V>` |
| Producer/consumer with backpressure | `BlockingCollection<T>` or `System.Threading.Channels` |
| Multi-step read-then-write atomicity | `lock` |
| Many readers, rare writers | `ReaderWriterLockSlim` |
| Limit concurrent access count | `SemaphoreSlim` |
| Simple counter / flag | `Interlocked` |
| Thread-local pool, same thread adds+removes | `ConcurrentBag<T>` |

---

## Layered Defense Pattern (used in this project)

The parking lot uses **two layers** rather than relying on one:

```
Layer 1 — Strategy (ConcurrentQueue.TryDequeue)
    Primary race guard. Atomic dequeue means two threads never get the same spot.
    No explicit lock needed here.

Layer 2 — ParkingFloorService (per-spot lock)
    Defensive fallback. Re-checks isOccupied before committing to the repository.
    Guards against state drift (crash recovery, manual edits) not covered by Layer 1.
```

This is a common pattern: **use the data structure's atomicity for the fast path**, add explicit locking only at the commit boundary as a safety net.
