using System.Collections.Concurrent;

namespace LowLevelDesign;

#region Enums
//enums
public enum VehicleType
{
    Bike,
    Car,
    Truck
}
public enum ParkingSpotType
{
    TwoWheeler,
    FourWheeler,
    LargeFourWheeler
}
public enum PricingType
{
    PerHour
}
public enum PaymentType
{
    Upi,
    CreditCard,
    DebitCard,
    Cash
}

#endregion

#region Entities
// Entities
public class Vehicle
{
    public string licensePlate { get; }
    public VehicleType type { get; }
    public string color { get; }

    public Vehicle(string licensePlate, VehicleType type, string color)
    {
        this.licensePlate = licensePlate;
        this.type = type;
        this.color = color;
    }
}

public class ParkingSpot
{
    public Guid id { get; }
    public int floorNumber { get; }
    public bool isOccupied { get; private set; }
    public string vehicleNumberPlate { get; private set; }
    public ParkingSpotType spotType { get; }

    public ParkingSpot(int floorNumber, ParkingSpotType spotType)
    {
        this.id = Guid.NewGuid();
        this.floorNumber = floorNumber;
        this.spotType = spotType;
    }

    public bool Park(string vehicleNumberPlate)
    {
        if (isOccupied)
            return false;
        isOccupied = true;
        this.vehicleNumberPlate = vehicleNumberPlate;
        return true;
    }

    public bool UnPark()
    {
        if (!isOccupied)
            return false;
        isOccupied = false;
        vehicleNumberPlate = null;
        return true;
    }
}

public class Floor
{
    public int floorNumber { get; }
    private readonly List<ParkingSpot> _parkingSpots = new();
    private readonly object _spotsLock = new();
    public IReadOnlyCollection<ParkingSpot> parkingSpots
    {
        get
        {
            lock (_spotsLock)
            {
                return _parkingSpots.ToList();
            }
        }
    }

    public Floor(int floorNumber, List<ParkingSpot> parkingSpots = null)
    {
        this.floorNumber = floorNumber;
        this._parkingSpots = parkingSpots == null ? new List<ParkingSpot>() : parkingSpots;
    }

    public bool AddParkingSpot(ParkingSpot parkingSpot)
    {
        lock (_spotsLock)
        {
            if (_parkingSpots.Any(spot => spot.id == parkingSpot.id))
                return false;
            _parkingSpots.Add(parkingSpot);
            return true;
        }
    }

    public bool IsParkingFull()
    {
        lock (_spotsLock)
        {
            return _parkingSpots.Count > 0 && _parkingSpots.All(spot => spot.isOccupied);
        }
    }
}

// NOTE: Gate was previously abstract with EntryGate/ExitGate as subclasses, but they
// share no real behavior - only an id field and constructor - and have no polymorphic
// use case (never treated as a List<Gate> or passed around as the base type). Forcing
// a shared Process() signature meant both subclasses had to override it just to throw,
// since EntryGate.Process(Vehicle) and ExitGate.Process(ticketId) have incompatible
// signatures. Standalone classes are the honest shape here - same enum-vs-subclass
// rule applied earlier to ParkingSpot and Vehicle: no shared behavior, no hierarchy.

public class EntryGate
{
    public string id { get; }
    private readonly IParkingLotService parkingLotService;

    public EntryGate(string id, IParkingLotService parkingLotService)
    {
        this.id = id;
        this.parkingLotService = parkingLotService;
    }

    public Ticket Process(Vehicle vehicle)
    {
        var ticket = parkingLotService.ParkVehicle(vehicle);
        if (ticket == null)
        {
            Console.WriteLine($"[EntryGate {id}] No spot available for {vehicle.licensePlate}.");
            return null;
        }
        Console.WriteLine($"[EntryGate {id}] Issued ticket {ticket.id} to {vehicle.licensePlate}.");
        return ticket;
    }
}

public class ExitGate
{
    public string id { get; }
    private readonly IParkingLotService parkingLotService;

    public ExitGate(string id, IParkingLotService parkingLotService)
    {
        this.id = id;
        this.parkingLotService = parkingLotService;
    }

    public bool Process(string ticketId)
    {
        var success = parkingLotService.UnParkVehicle(ticketId);
        Console.WriteLine(success
            ? $"[ExitGate {id}] Ticket {ticketId} closed, spot freed."
            : $"[ExitGate {id}] Failed to close ticket {ticketId}.");
        return success;
    }
}

public class Ticket
{
    public Guid id { get; }
    public Guid parkingSpotId { get; }
    public int floorNumber { get; }
    public string vehicleNumberPlate { get; }
    public VehicleType vehicleType { get; }
    public DateTime parkedAt { get; }
    public DateTime? unParkedAt { get; private set; }
    public decimal amount { get; private set; }
    public string currency { get; private set; }

    public Ticket(Guid parkingSpotId, int floorNumber, string vehicleNumberPlate, VehicleType vehicleType, DateTime parkedAt)
    {
        id = Guid.NewGuid();
        this.vehicleNumberPlate = vehicleNumberPlate;
        this.parkingSpotId = parkingSpotId;
        this.floorNumber = floorNumber;
        this.vehicleType = vehicleType;
        this.parkedAt = parkedAt;
    }

    public void UnPark(DateTime unParkedAt, decimal amount, string currency)
    {
        this.unParkedAt = unParkedAt;
        this.amount = amount;
        this.currency = currency;
    }
}

#endregion

#region Interfaces
//Interfaces
public interface IParkingStrategy
{
    ParkingSpot GetParkingSpot(VehicleType vehicleType, string gateId = null);
    bool AddParkingSpot(ParkingSpot parkingSpot); // re-enqueue on unpark, parkingSpot already has floorNumber
}
public interface IPriceStrategy
{
    decimal CalculatePrice(Ticket ticket);
}
public interface IParkingFloorRepository
{
    bool AddFloor(int floorNumber);
    bool AddParkingSpot(int floorNumber, ParkingSpot spot);
    ParkingSpot GetSpot(int floorNumber, Guid spotId);
    bool UpdateSpotStatus(int floorNumber, Guid spotId, bool isOccupied, string vehicleNumberPlate);
    List<ParkingSpot> GetAllSpots(int floorNumber);
    Floor GetFloor(int floorNumber);
    bool IsFloorFull(int floorNumber);
}
public interface ITicketRepository
{
    Ticket AddTicket(Ticket ticket);
    Ticket UpdateTicket(Ticket ticket);
    Ticket GetTicket(Guid ticketId);
}

public interface IParkingLotService
{
    Ticket ParkVehicle(Vehicle vehicle);
    bool UnParkVehicle(string ticketId);
}

public interface IParkingFloorService
{
    bool AddFloor(int floorNumber);
    bool AddParkingSpot(int floorNumber, ParkingSpot spot);
    bool ParkVehicleAtSpot(int floorNumber, Guid parkingSpotId, string vehicleNumberPlate);
    // lock spotId -> re-check isOccupied -> persist via repository -> unlock
    ParkingSpot FreeSpot(int floorNumber, Guid parkingSpotId);
    // lock spotId -> mark unoccupied via repository -> unlock -> return the freed spot
    // so the caller can re-enqueue the SAME instance into the strategy (not a new one)
}

public interface ITicketService
{
    Ticket CreateTicket(Guid parkingSpotId, int floorNumber, string vehicleNumberPlate, VehicleType vehicleType);
    Ticket GetTicket(string ticketId);
    Ticket UpdateTicket(string ticketId, DateTime unParkedAt, decimal amount, string currency);
}

public interface IPaymentService
{
    bool MakePayment(decimal amount, PaymentType type, string ticketId);
}


#endregion

#region Strategies
//Strategies
public class PerHourPricing : IPriceStrategy
{
    private const decimal _twoWheelerRate = 10;
    private const decimal _fourWheelerRate = 20;
    private const decimal _largeFourWheelerRate = 30;

    public decimal CalculatePrice(Ticket ticket)
    {
        if (ticket == null)
            throw new ArgumentNullException(nameof(ticket), "Ticket cannot be null.");
        if (ticket.unParkedAt == null)
            throw new ArgumentException("Ticket has not been unparked yet.");
        if (ticket.unParkedAt < ticket.parkedAt)
            throw new ArgumentException("Unparked time cannot be earlier than parked time.");

        // Round up partial hours - industry-standard billing (1hr 5min charges as 2 hours)
        double totalHours = (ticket.unParkedAt.Value - ticket.parkedAt).TotalHours;
        int hours = (int)Math.Ceiling(totalHours);
        if (hours == 0) hours = 1; // minimum 1 hour charge

        return ticket.vehicleType switch
        {
            VehicleType.Bike => hours * _twoWheelerRate,
            VehicleType.Car => hours * _fourWheelerRate,
            VehicleType.Truck => hours * _largeFourWheelerRate,
            _ => throw new ArgumentException("Invalid vehicle type.")
        };
    }
}

public class AvailableParkingSlot : IParkingStrategy
{
    // ConcurrentQueue.TryDequeue is atomic - this is the actual race-condition fix.
    // Two threads calling GetParkingSpot() concurrently can never receive the same spot,
    // because the dequeue itself is the synchronization point (no separate lock needed).
    private readonly ConcurrentQueue<ParkingSpot> _availableSpotsTwoWheeler = new();
    private readonly ConcurrentQueue<ParkingSpot> _availableSpotsFourWheeler = new();
    private readonly ConcurrentQueue<ParkingSpot> _availableSpotsLargeFourWheeler = new();

    public AvailableParkingSlot(List<ParkingSpot> availableSpots)
    {
        foreach (var parkingSpot in availableSpots)
        {
            EnqueueByType(parkingSpot);
        }
    }

    public ParkingSpot GetParkingSpot(VehicleType vehicleType, string gateId = null)
    {
        var queue = QueueFor(vehicleType);
        if (queue == null)
        {
            Console.WriteLine("Invalid vehicle type.");
            return null;
        }
        return queue.TryDequeue(out var spot) ? spot : null;
    }

    public bool AddParkingSpot(ParkingSpot parkingSpot)
    {
        return EnqueueByType(parkingSpot);
    }

    private bool EnqueueByType(ParkingSpot parkingSpot)
    {
        var queue = QueueFor(SpotTypeToVehicleType(parkingSpot.spotType));
        if (queue == null)
        {
            Console.WriteLine("Invalid parking spot type.");
            return false;
        }
        queue.Enqueue(parkingSpot);
        return true;
    }

    private ConcurrentQueue<ParkingSpot> QueueFor(VehicleType vehicleType)
    {
        return vehicleType switch
        {
            VehicleType.Bike => _availableSpotsTwoWheeler,
            VehicleType.Car => _availableSpotsFourWheeler,
            VehicleType.Truck => _availableSpotsLargeFourWheeler,
            _ => null
        };
    }

    private static VehicleType SpotTypeToVehicleType(ParkingSpotType spotType)
    {
        return spotType switch
        {
            ParkingSpotType.TwoWheeler => VehicleType.Bike,
            ParkingSpotType.FourWheeler => VehicleType.Car,
            ParkingSpotType.LargeFourWheeler => VehicleType.Truck,
            _ => throw new ArgumentException("Invalid parking spot type.")
        };
    }
}

#endregion

#region Repositories
//Repositories
public class ParkingFloorRepository : IParkingFloorRepository
{
    private readonly ConcurrentDictionary<int, Floor> _floors = new();

    public bool AddFloor(int floorNumber)
    {
        if (_floors.ContainsKey(floorNumber))
            return false;
        return _floors.TryAdd(floorNumber, new Floor(floorNumber));
    }

    public bool AddParkingSpot(int floorNumber, ParkingSpot spot)
    {
        if (!_floors.TryGetValue(floorNumber, out var floor))
            return false;
        // dedup-check and list mutation now live inside Floor.AddParkingSpot,
        // not duplicated here
        return floor.AddParkingSpot(spot);
    }

    public ParkingSpot GetSpot(int floorNumber, Guid spotId)
    {
        if (!_floors.TryGetValue(floorNumber, out var floor))
            return null;
        return floor.parkingSpots.FirstOrDefault(spot => spot.id == spotId);
    }

    public bool UpdateSpotStatus(int floorNumber, Guid spotId, bool isOccupied, string vehicleNumberPlate)
    {
        var spot = GetSpot(floorNumber, spotId);
        if (spot == null)
            return false;

        return isOccupied ? spot.Park(vehicleNumberPlate) : spot.UnPark();
    }

    public List<ParkingSpot> GetAllSpots(int floorNumber)
    {
        return _floors.TryGetValue(floorNumber, out var floor)
            ? floor.parkingSpots.ToList()
            : new List<ParkingSpot>();
    }

    public Floor GetFloor(int floorNumber)
    {
        return _floors.TryGetValue(floorNumber, out var floor) ? floor : null;
    }

    public bool IsFloorFull(int floorNumber)
    {
        // business rule delegated to Floor, repository just looks it up
        return _floors.TryGetValue(floorNumber, out var floor) && floor.IsParkingFull();
    }
}

public class TicketRepository : ITicketRepository
{
    private readonly ConcurrentDictionary<Guid, Ticket> _tickets = new();

    public Ticket AddTicket(Ticket ticket)
    {
        return _tickets.TryAdd(ticket.id, ticket) ? ticket : null;
    }

    public Ticket UpdateTicket(Ticket ticket)
    {
        if (ticket == null)
            return null;

        if (!_tickets.TryGetValue(ticket.id, out var existingTicket))
            return null;

        return _tickets.TryUpdate(ticket.id, ticket, existingTicket)
            ? ticket
            : null;
    }

    public Ticket GetTicket(Guid ticketId)
    {
        return _tickets.TryGetValue(ticketId, out var ticket) ? ticket : null;
    }
}

#endregion

#region Services
//Services

public class ParkingFloorService : IParkingFloorService
{
    private readonly IParkingFloorRepository _floorRepository;
    // Per-spot lock objects. This is the DEFENSIVE FALLBACK layer - primary
    // race protection already happened at strategy-dequeue time. This guards
    // against state drift (e.g. manual repo edits, crash recovery) rather than
    // being the main concurrency mechanism.
    private readonly ConcurrentDictionary<Guid, object> _spotLocks = new();

    public ParkingFloorService(IParkingFloorRepository floorRepository)
    {
        _floorRepository = floorRepository;
    }

    public bool AddFloor(int floorNumber)
    {
        return _floorRepository.AddFloor(floorNumber);
    }

    public bool AddParkingSpot(int floorNumber, ParkingSpot spot)
    {
        return _floorRepository.AddParkingSpot(floorNumber, spot);
    }

    public bool ParkVehicleAtSpot(int floorNumber, Guid parkingSpotId, string vehicleNumberPlate)
    {
        var lockObj = _spotLocks.GetOrAdd(parkingSpotId, _ => new object());
        lock (lockObj)
        {
            var spot = _floorRepository.GetSpot(floorNumber, parkingSpotId);
            if (spot == null || spot.isOccupied)
                return false;

            return _floorRepository.UpdateSpotStatus(floorNumber, parkingSpotId, true, vehicleNumberPlate);
        }
    }

    public ParkingSpot FreeSpot(int floorNumber, Guid parkingSpotId)
    {
        var lockObj = _spotLocks.GetOrAdd(parkingSpotId, _ => new object());
        lock (lockObj)
        {
            var spot = _floorRepository.GetSpot(floorNumber, parkingSpotId);
            if (spot == null || !spot.isOccupied)
                return null;

            var freed = _floorRepository.UpdateSpotStatus(floorNumber, parkingSpotId, false, null);
            return freed ? spot : null;
        }
    }
}

public class TicketService : ITicketService
{
    private readonly ITicketRepository _ticketRepository;

    public TicketService(ITicketRepository ticketRepository)
    {
        _ticketRepository = ticketRepository;
    }

    public Ticket CreateTicket(Guid parkingSpotId, int floorNumber, string vehicleNumberPlate, VehicleType vehicleType)
    {
        var ticket = new Ticket(parkingSpotId, floorNumber, vehicleNumberPlate, vehicleType, DateTime.UtcNow);
        return _ticketRepository.AddTicket(ticket);
    }

    public Ticket GetTicket(string ticketId)
    {
        if (!Guid.TryParse(ticketId, out var id))
            return null;
        return _ticketRepository.GetTicket(id);
    }

    public Ticket UpdateTicket(string ticketId, DateTime unParkedAt, decimal amount, string currency)
    {
        var ticket = GetTicket(ticketId);
        if (ticket == null)
            return null;

        ticket.UnPark(unParkedAt, amount, currency);
        return _ticketRepository.UpdateTicket(ticket);
    }
}

public class PaymentService : IPaymentService
{
    // Dummy implementation - real implementation would call a payment gateway
    // and use ticketId for idempotency (avoid double-charging on retry).
    public bool MakePayment(decimal amount, PaymentType type, string ticketId)
    {
        Console.WriteLine($"[PaymentService] Charged {amount} via {type} for ticket {ticketId}.");
        return true;
    }
}

public class ParkingLotService : IParkingLotService
{
    private readonly IParkingStrategy _parkingStrategy;
    private readonly IParkingFloorService _floorService;
    private readonly ITicketService _ticketService;
    private readonly IPriceStrategy _priceStrategy;
    private readonly IPaymentService _paymentService;

    public ParkingLotService(
        IParkingStrategy parkingStrategy,
        IParkingFloorService floorService,
        ITicketService ticketService,
        IPriceStrategy priceStrategy,
        IPaymentService paymentService)
    {
        _parkingStrategy = parkingStrategy;
        _floorService = floorService;
        _ticketService = ticketService;
        _priceStrategy = priceStrategy;
        _paymentService = paymentService;
    }

    public Ticket ParkVehicle(Vehicle vehicle)
    {
        // Retry loop: strategy dequeue is the primary defense (atomic), floorService
        // commit-check is the defensive fallback. On rare rejection, try the next spot.
        const int maxAttempts = 5;
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            var spot = _parkingStrategy.GetParkingSpot(vehicle.type);
            if (spot == null)
                return null; // no spots available for this vehicle type

            var committed = _floorService.ParkVehicleAtSpot(spot.floorNumber, spot.id, vehicle.licensePlate);
            if (committed)
            {
                return _ticketService.CreateTicket(spot.id, spot.floorNumber, vehicle.licensePlate, vehicle.type);
            }
            // Lost the race (shouldn't normally happen given atomic dequeue) - do NOT
            // re-add this spot to the strategy, it's genuinely occupied. Try again.
        }
        return null;
    }

    public bool UnParkVehicle(string ticketId)
    {
        var ticket = _ticketService.GetTicket(ticketId);
        if (ticket == null)
            return false;

        var freedSpot = _floorService.FreeSpot(ticket.floorNumber, ticket.parkingSpotId);
        if (freedSpot == null)
            return false;

        // Re-enqueue the SAME spot instance returned by FreeSpot - not a newly
        // constructed one. A new ParkingSpot() would get a new Guid id that doesn't
        // exist in the repository, creating a phantom entry in the strategy's queue.
        _parkingStrategy.AddParkingSpot(freedSpot);

        var unparkedAt = DateTime.UtcNow;
        var tempTicket = new Ticket(ticket.parkingSpotId, ticket.floorNumber, ticket.vehicleNumberPlate, ticket.vehicleType, ticket.parkedAt);
        tempTicket.UnPark(unparkedAt, 0, "INR");
        var amount = _priceStrategy.CalculatePrice(tempTicket);

        _paymentService.MakePayment(amount, PaymentType.Upi, ticketId);
        _ticketService.UpdateTicket(ticketId, unparkedAt, amount, "INR");

        return true;
    }
}
#endregion

public class ParkingLot
{
    private readonly IParkingFloorService _floorService;
    private readonly IParkingLotService _parkingLotService;
    private readonly IParkingStrategy _parkingStrategy;
    private readonly List<EntryGate> _entryGates;
    private readonly List<ExitGate> _exitGates;

    public ParkingLot(
        IParkingFloorService floorService,
        IParkingLotService parkingLotService,
        IParkingStrategy parkingStrategy,
        List<EntryGate> entryGates,
        List<ExitGate> exitGates)
    {
        _floorService = floorService;
        _parkingLotService = parkingLotService;
        _parkingStrategy = parkingStrategy;
        _entryGates = entryGates;
        _exitGates = exitGates;
    }

    public bool AddFloor(int floorNumber) => _floorService.AddFloor(floorNumber);

    public bool AddParkingSpot(int floorNumber, ParkingSpot spot)
    {
        var added = _floorService.AddParkingSpot(floorNumber, spot);
        if (added)
            _parkingStrategy.AddParkingSpot(spot);
        return added;
    }

    public Ticket ParkVehicle(Vehicle vehicle) => _parkingLotService.ParkVehicle(vehicle);

    public bool UnParkVehicle(string ticketId) => _parkingLotService.UnParkVehicle(ticketId);

    // ------------------------------------------------------------------
    // Console entry point - ParkingLot now owns the interaction loop.
    // Program.cs just builds a ParkingLot and calls Start().
    // ------------------------------------------------------------------
    public void Start()
    {
        bool running = true;
        while (running)
        {
            Console.WriteLine();
            Console.WriteLine("==== Parking Lot ====");
            Console.WriteLine("1. Park a vehicle (Entry Gate)");
            Console.WriteLine("2. Unpark a vehicle (Exit Gate)");
            Console.WriteLine("3. Exit");
            Console.Write("Choose an option: ");

            var choice = Console.ReadLine();
            switch (choice)
            {
                case "1":
                    HandlePark();
                    break;
                case "2":
                    HandleUnpark();
                    break;
                case "3":
                    running = false;
                    break;
                default:
                    Console.WriteLine("Invalid option, try again.");
                    break;
            }
        }
    }

    private void HandlePark()
    {
        var gate = SelectGate(_entryGates, "entry");
        if (gate == null) return;

        Console.Write("License plate: ");
        var plate = Console.ReadLine();

        Console.Write("Vehicle type (1=Bike, 2=Car, 3=Truck): ");
        var typeChoice = Console.ReadLine();
        VehicleType? type = typeChoice switch
        {
            "1" => VehicleType.Bike,
            "2" => VehicleType.Car,
            "3" => VehicleType.Truck,
            _ => null
        };
        if (type == null)
        {
            Console.WriteLine("Invalid vehicle type.");
            return;
        }

        Console.Write("Color: ");
        var color = Console.ReadLine();

        var vehicle = new Vehicle(plate, type.Value, color);
        var ticket = gate.Process(vehicle);
        if (ticket != null)
            Console.WriteLine($"Ticket issued: {ticket.id}");
    }

    private void HandleUnpark()
    {
        var gate = SelectGate(_exitGates, "exit");
        if (gate == null) return;

        Console.Write("Ticket id: ");
        var ticketId = Console.ReadLine();
        gate.Process(ticketId);
    }

    private static EntryGate SelectGate(List<EntryGate> gates, string label)
    {
        if (gates == null || gates.Count == 0)
        {
            Console.WriteLine($"No {label} gates configured.");
            return null;
        }
        if (gates.Count == 1)
            return gates[0];

        Console.WriteLine($"Select {label} gate:");
        for (int i = 0; i < gates.Count; i++)
            Console.WriteLine($"{i + 1}. {gates[i].id}");
        Console.Write("Choice: ");
        if (int.TryParse(Console.ReadLine(), out var idx) && idx >= 1 && idx <= gates.Count)
            return gates[idx - 1];

        Console.WriteLine("Invalid gate selection.");
        return null;
    }

    private static ExitGate SelectGate(List<ExitGate> gates, string label)
    {
        if (gates == null || gates.Count == 0)
        {
            Console.WriteLine($"No {label} gates configured.");
            return null;
        }
        if (gates.Count == 1)
            return gates[0];

        Console.WriteLine($"Select {label} gate:");
        for (int i = 0; i < gates.Count; i++)
            Console.WriteLine($"{i + 1}. {gates[i].id}");
        Console.Write("Choice: ");
        if (int.TryParse(Console.ReadLine(), out var idx) && idx >= 1 && idx <= gates.Count)
            return gates[idx - 1];

        Console.WriteLine("Invalid gate selection.");
        return null;
    }
}