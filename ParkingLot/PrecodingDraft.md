# Parking Lot LLD — Pre-Coding Draft

## Requirements

**Functional**
1. Support **Motorcycle, Car, Truck** vehicle types
2. **Multi-floor** parking lot, each floor has multiple spots
3. Spots are **typed by size** — Small (Motorcycle), Medium (Car), Large (Truck). A larger spot cannot accommodate a smaller vehicle
4. **Multiple entry/exit gates**
5. Spot assignment — any available spot of the matching size (no nearest-first optimization needed for v1)
6. Generate a **ticket on entry** with timestamp; use it for **fee calculation on exit**
7. **Hourly fee calculation**, extensible for future pricing strategies
8. Real-time tracking of **available spot count per floor**

**Non-Functional**
1. Handle **concurrency** — no two vehicles should ever get assigned the same spot
2. System should be **extensible** — new vehicle types, spot types, and fee strategies should be easy to add without touching existing classes

---

## Enums

```
VehicleType      : Bike, Car, Truck
ParkingSpotType  : TwoWheeler, FourWheeler, LargeFourWheeler
PricingType      : PerHour
PaymentType      : UPI, CreditCard, DebitCard, Cash
```

---

## Entities

### `Vehicle` (class — NOT abstract)

> No behavioral divergence across Bike/Car/Truck today — only `VehicleType` differs (same reasoning as `ParkingSpot`). Enum is correct, not subclassing → avoids YAGNI violation. Revisit only if a vehicle type needs distinct behavior (e.g. EV charging logic).

**Fields:**
- `string licensePlate`
- `VehicleType type`
- `string color`

**Methods:** *(none)*

---

### `ParkingSpot` (class — NOT abstract)

> Collapsed `SmallSpot/MediumSpot/LargeSpot` into a single class — no behavioral difference between sizes, only a `SpotType` value.

**Fields:**
- `Guid id`
- `int floorNumber`
- `bool isOccupied` *(property, not a method)*
- `string vehicleNumberPlate`
- `ParkingSpotType spotType`

**Methods:**
- `bool Park(string vehicleNumberPlate)`
- `bool UnPark()`

---

### `Floor` (class)

**Fields:**
- `int floorNumber`
- `List<ParkingSpot> parkingSpots`

**Methods:**
- `bool AddParkingSpot(ParkingSpot parkingSpot)`
- `bool isParkingFull()`

---

### `EntryGate` / `ExitGate` (standalone classes — NOT a shared hierarchy)

> Originally planned as `abstract Gate` with subclasses, but dropped: they share only an `id` field and are never used polymorphically (no `List<Gate>`). More importantly, their `Process()` signatures are incompatible — `EntryGate.Process(Vehicle)` vs `ExitGate.Process(string ticketId)` — so forcing a shared base meant both overrides would throw. Standalone classes are the honest shape.

**`EntryGate` fields:**
- `string id`
- `IParkingLotService parkingLotService` *(injected)*

**`EntryGate` methods:**
- `Ticket Process(Vehicle vehicle)` — calls `ParkingLotService.ParkVehicle`, prints result, returns ticket

**`ExitGate` fields:**
- `string id`
- `IParkingLotService parkingLotService` *(injected)*

**`ExitGate` methods:**
- `bool Process(string ticketId)` — calls `ParkingLotService.UnParkVehicle`, prints result, returns success

---

### `Ticket` (class)

**Fields:**
- `Guid id`
- `Guid parkingSpotId`
- `int floorNumber`
- `string vehicleNumberPlate`
- `VehicleType vehicleType`
- `DateTime parkedAt`
- `DateTime? unParkedAt` *(nullable — null until vehicle exits)*
- `decimal amount`
- `string currency`

**Methods:**
- `void UnPark(DateTime unParkedAt, decimal amount, string currency)` — sets exit fields

---

## Service / Repository / Strategy Interfaces

### `IParkingLotService` *(orchestrator)*

```
Ticket ParkVehicle(Vehicle vehicle)
bool UnParkVehicle(string ticketId)
```

---

### `ITicketService`

```
Ticket CreateTicket(Guid parkingSpotId, int floorNumber, string vehicleNumberPlate, VehicleType vehicleType)
Ticket GetTicket(string ticketId)
Ticket UpdateTicket(string ticketId, DateTime unParkedAt, decimal amount, string currency)
```

---

### `IParkingFloorService`

> Owns locking + double-checking occupancy before delegating to repository. This is where concurrency-safe *commit* happens — not spot discovery (that belongs to the strategy).

```
bool AddFloor(int floorNumber)
bool AddParkingSpot(int floorNumber, ParkingSpot spot)
bool ParkVehicleAtSpot(int floorNumber, Guid parkingSpotId, string vehicleNumberPlate)
    // lock spotId -> re-check isOccupied -> persist via repository -> unlock
ParkingSpot FreeSpot(int floorNumber, Guid parkingSpotId)
    // lock spotId -> mark unoccupied via repository -> unlock -> return the freed spot
    // returns the SAME instance so caller can re-enqueue it into the strategy
    // (a new ParkingSpot() would get a new Guid not in the repository)
```

---

### `IPaymentService` *(dummy, for e2e working flow)*

```
bool MakePayment(decimal amount, PaymentType type, string ticketId)
```

---

### `IParkingStrategy`

> In-memory data structure lives inside the strategy class so each strategy can store spots in its own way. Multiple strategies possible: `FirstAvailableStrategy`, `NearestFirstStrategy`, `RandomStrategy`, etc.
>
> **Dequeue is atomic** — happens at *selection time*, not after commit. This is what prevents two threads from ever receiving the same spot — the in-memory structure itself becomes the synchronization point.

```
ParkingSpot GetParkingSpot(VehicleType vehicleType, string gateId = null)
    // removes/dequeues spot from in-memory DS at selection time
bool AddParkingSpot(ParkingSpot parkingSpot)
    // re-enqueue on unpark; spot already carries floorNumber, no extra param needed
```

---

### `IPriceStrategy`

```
decimal CalculatePrice(Ticket ticket)
```

---

### `IParkingFloorRepository`

> Pure data access only — no locking, no business rules.

```
bool AddFloor(int floorNumber)
bool AddParkingSpot(int floorNumber, ParkingSpot spot)
ParkingSpot GetSpot(int floorNumber, Guid spotId)
bool UpdateSpotStatus(int floorNumber, Guid spotId, bool isOccupied, string vehicleNumberPlate)
List<ParkingSpot> GetAllSpots(int floorNumber)
Floor GetFloor(int floorNumber)
bool IsFloorFull(int floorNumber)
```

---

### `ITicketRepository`

```
Ticket AddTicket(Ticket ticket)
Ticket UpdateTicket(Ticket ticket)
Ticket GetTicket(Guid ticketId)
```

---

## End-to-End Flow Summary

**Park:**
```
ParkingLotService.ParkVehicle(vehicle)
  loop (up to maxAttempts):
    spot = strategy.GetParkingSpot(vehicleType)        // atomic dequeue
    if spot == null → fail, no spots available
    committed = floorService.ParkVehicleAtSpot(spot.floorNumber, spot.id, plate)
    if committed:
        return ticketService.CreateTicket(spot.id, spot.floorNumber, plate, vehicleType)
    // lost race (rare) — spot is genuinely occupied, do NOT re-enqueue, try next
```

**Unpark:**
```
ParkingLotService.UnParkVehicle(ticketId)
  ticket = ticketService.GetTicket(ticketId)
  freedSpot = floorService.FreeSpot(ticket.floorNumber, ticket.parkingSpotId)
  strategy.AddParkingSpot(freedSpot)             // re-enqueue the SAME instance
  // build a temp ticket copy to calculate price (original ticket not yet updated)
  amount = priceStrategy.CalculatePrice(tempTicket)
  paymentService.MakePayment(amount, PaymentType.Upi, ticketId)
  ticketService.UpdateTicket(ticketId, unParkedAt, amount, "INR")
```

---

## `ParkingLot` (facade + composition root)

> Wraps all services and gates behind a simple public API. Also owns the console interaction loop — `Program.cs` just builds and calls `Start()`.

**Fields:** `IParkingFloorService`, `IParkingLotService`, `IParkingStrategy`, `List<EntryGate>`, `List<ExitGate>`

**Public API:**
```
bool AddFloor(int floorNumber)
bool AddParkingSpot(int floorNumber, ParkingSpot spot)  // also enqueues spot into strategy
Ticket ParkVehicle(Vehicle vehicle)
bool UnParkVehicle(string ticketId)
void Start()   // console interaction loop
```

---

## Build Order

```
Enums → Entities → Repository (interface + impl) → Strategy (interface + impl)
→ Service (interface + impl) → Orchestrator → Entry/Boundary layer (Gate)
→ Driver/Main (DI wiring)
```