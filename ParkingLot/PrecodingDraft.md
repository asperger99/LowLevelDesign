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
- `string numberPlate`
- `VehicleType type`
- `string Color`

**Methods:** *(none)*

---

### `ParkingSpot` (class — NOT abstract)

> Collapsed `SmallSpot/MediumSpot/LargeSpot` into a single class — no behavioral difference between sizes, only a `SpotType` value.

**Fields:**
- `string Id`
- `int FloorNumber`
- `bool isOccupied`
- `string vehicleNumberPlate`
- `ParkingSpotType spotType`

**Methods:**
- `bool Park(string vehicleNumberPlate)`
- `bool Unpark()`
- `bool IsOccupied()`

---

### `Floor` (class)

**Fields:**
- `int floorNumber`
- `List<ParkingSpot> parkingSpots`

**Methods:**
- `bool AddParkingSpot(ParkingSpot parkingSpot)`
- `bool isParkingFull()`

---

### `Gate` (abstract — inherited by `EntryGate`, `ExitGate`)

**Fields:**
- `string id`

**Methods:**
- `process()` — for `EntryGate`: processes parking + generates ticket by calling `ParkingLotService`. For `ExitGate`: frees up the parking spot, completes payment, updates ticket by calling the responsible services.

---

### `Ticket` (class)

**Fields:**
- `string id`
- `string parkingSpotId`
- `string vehicleNumberPlate`
- `VehicleType vehicleType`
- `DateTime parkedAt`
- `DateTime unParkedAt`
- `decimal Amount`
- `string currency`

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
Ticket CreateTicket(string parkingSpotId, string vehicleNumberPlate, VehicleType vehicleType)
Ticket GetTicket(string ticketId)
Ticket UpdateTicket(string ticketId, DateTime unParkedAt, decimal amount)
```

---

### `IParkingFloorService`

> Owns locking + double-checking occupancy before delegating to repository. This is where concurrency-safe *commit* happens — not spot discovery (that belongs to the strategy).

```
bool AddFloor(int floorNumber)
bool AddParkingSpot(int floorNumber, ParkingSpot spot)
bool ParkVehicleAtSpot(int floorNumber, string parkingSpotId, string vehicleNumberPlate)
    // lock spotId -> re-check isOccupied -> persist via repository -> unlock
bool FreeSpot(int floorNumber, string parkingSpotId)
    // lock spotId -> mark unoccupied via repository -> unlock
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
bool AddSpotBack(int floorNumber, ParkingSpot spot)
    // re-enqueue on unpark
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
ParkingSpot GetSpot(int floorNumber, string spotId)
bool UpdateSpotStatus(int floorNumber, string spotId, bool isOccupied, string vehicleNumberPlate)
List<ParkingSpot> GetAllSpots(int floorNumber)
```

---

### `ITicketRepository`

```
AddTicket(Ticket ticket)
UpdateTicket(Ticket ticket)
```

---

## End-to-End Flow Summary

**Park:**
```
ParkingLotService.ParkVehicle(vehicle)
  loop:
    spot = strategy.GetParkingSpot(vehicleType)   // atomic dequeue
    if spot == null → fail, no spots available
    success = floorService.ParkVehicleAtSpot(floor, spot.Id, plate)
    if success:
        ticket = ticketService.CreateTicket(spot.Id, plate, vehicleType)
        return ticket
    else:
        continue loop  // defensive fallback, try next spot
```

**Unpark:**
```
ParkingLotService.UnparkVehicle(ticketId)
  ticket = ticketService.GetTicket(ticketId)
  floorService.FreeSpot(ticket.floorNumber, ticket.parkingSpotId)
  strategy.AddSpotBack(ticket.floorNumber, spot)
  amount = priceStrategy.CalculatePrice(ticket)
  paymentService.MakePayment(amount, ...)
  ticketService.UpdateTicket(...)
```

---

## Build Order

```
Enums → Entities → Repository (interface + impl) → Strategy (interface + impl)
→ Service (interface + impl) → Orchestrator → Entry/Boundary layer (Gate)
→ Driver/Main (DI wiring)
```