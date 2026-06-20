# Parking Lot LLD — Key Takeaways (Pre-Coding)

## 1. Subclass vs. Enum — the core decision rule

> If subclasses would only differ in a field value with identical behavior, use an enum. If behavior genuinely diverges, use inheritance.

`ParkingSpot` was the first place this showed up — `SmallSpot/MediumSpot/LargeSpot` collapsed into one class + a `SpotType` enum, since there was no actual behavioral difference between sizes.

`Vehicle` was trickier. It got justified as abstract early on using a hypothetical future need (EV charging, sidecars, hazardous cargo flags) — but none of that is in scope today, and `Vehicle` has zero methods. Once the same enum-vs-subclass rule got applied honestly, it was clear `Vehicle` should collapse too, down to a single class + `VehicleType` enum.

Worth remembering: catching your own inconsistency mid-design (like with `Vehicle`) is a better interview signal than getting it right on the first try.

---

## 2. Repository vs. Service — strict boundary

> Repository = pure data access (CRUD), no business rules, no locking. Service = business rules, validation, concurrency control, sitting in front of a "dumb" repository.

There was a tempting shortcut here — dropping `IParkingFloorService` entirely and pushing validation straight into the repository, just to keep `ParkingLotService` light. That ended up getting reversed: concurrency-aware logic baked into a repository makes it harder to swap implementations later and blurs what the repository is actually for. `IParkingFloorService` earns its keep by owning **locking + double-checking occupancy** before handing off to a repository that just stores data.

---

## 3. Single source of truth for spot availability

Two options were on the table:

- **Option A** — Repository as single source of truth (query on every `findSpot()` call)
- **Option B** — Strategy holds in-memory state, repository only for persistence/recovery *(the one we went with)*

Option B wins on performance — an in-memory lookup is O(1) versus a DB hit on every park request — and it fits the principle that a **repository's job is durability, not real-time business logic**.

---

## 4. Concurrency — where the actual fix lives

This was the most important correction in the whole design:

> Don't dequeue-after-commit. **Dequeue at selection time**, inside the strategy, using an atomic operation (`ConcurrentQueue.TryDequeue` or equivalent lock). That turns the in-memory structure itself into the synchronization point — two threads can never receive the same spot in the first place.

`IParkingFloorService.ParkVehicleAtSpot()`'s lock-and-recheck is a **defensive fallback**, not the primary defense. Knowing the difference between the two is what makes the answer airtight instead of just "I added a lock somewhere."

---

## 5. Strategy pattern hygiene

The early draft had **two overloaded `GetParkingSpot` methods** on `IParkingStrategy` — one with `gateId`, one without. That's a smell: it forces callers to know which strategy variant they're talking to, which defeats the whole point of the Strategy pattern. It collapsed down to **one signature** with an optional parameter, leaving each concrete strategy free to use it or ignore it.

> **Rule:** a Strategy-pattern interface should have a single, stable signature no matter which concrete strategy is plugged in.

---

## 6. Signature design — work backward from the actual flow

A few early service signatures looked fine structurally but didn't actually match how they'd get called:

- `ParkVehicle(spotId, plate)` assumed the caller already had a `spotId` — but the orchestrator only gets that internally, from the strategy. Fixed to `ParkVehicle(Vehicle vehicle)`.
- `UnParkVehicle(spotId)` assumed the driver hands over a spot ID — but they hand over a **ticket**. Fixed to `UnParkVehicle(ticketId)`.
- `ITicketService` was missing `GetTicket()` altogether, even though `UnParkVehicle` can't resolve `ticketId → spotId/floor` without it.

**Rule of thumb:** don't lock in a method signature until you've mentally walked the full caller → callee chain for that exact flow.

---

## 7. Naming consistency signals care

Small things kept popping up — `vehicleId` vs `vehicleNumberPlate`, `TicketId` vs `ticketId` casing, `Floor.isParkingFull` being both a field and a method name. None of it breaks the design, but in an interview, repeated small inconsistencies read as lack of attention to detail. Worth a final pass before calling any draft "done."

---

## 8. Standard build order (reusable for any LLD problem)

```
Enums → Entities → Repository (interface + impl) → Strategy (interface + impl)
→ Service (interface + impl) → Orchestrator → Entry/Boundary layer → Driver/Main (DI wiring)
```

Bottom-up, dependency-first — avoids forward references and mirrors how you'd narrate the build live in an interview.