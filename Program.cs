using LowLevelDesign;

// ============================================================================
// COMPOSITION ROOT - wiring order:
// 1. Repository first (needs nothing)
// 2. Floors + spots added to repository
// 3. Strategy built FROM repository's spots (strategy needs the initial pool)
// 4. Services built on top of repository
// 5. ParkingLotService (orchestrator) wired with strategy + services
// 6. ParkingLot (composition root) wraps everything, exposes simple API
// 7. Gates wrap ParkingLotService for the "vehicle-facing" entry/exit flow
// ============================================================================

// 1. Repository
IParkingFloorRepository floorRepository = new ParkingFloorRepository();
IParkingFloorService floorService = new ParkingFloorService(floorRepository);

// 2. Set up floors and spots BEFORE building the strategy
floorService.AddFloor(1);
floorService.AddFloor(2);

var floor1Spots = new List<ParkingSpot>
{
    new ParkingSpot(1, ParkingSpotType.TwoWheeler),
    new ParkingSpot(1, ParkingSpotType.TwoWheeler),
    new ParkingSpot(1, ParkingSpotType.FourWheeler),
    new ParkingSpot(1, ParkingSpotType.FourWheeler),
};
var floor2Spots = new List<ParkingSpot>
{
    new ParkingSpot(2, ParkingSpotType.FourWheeler),
    new ParkingSpot(2, ParkingSpotType.LargeFourWheeler),
};

foreach (var spot in floor1Spots) floorService.AddParkingSpot(1, spot);
foreach (var spot in floor2Spots) floorService.AddParkingSpot(2, spot);

// 3. Strategy needs the full initial pool of spots up front
var allSpots = floorRepository.GetAllSpots(1).Concat(floorRepository.GetAllSpots(2)).ToList();
IParkingStrategy parkingStrategy = new AvailableParkingSlot(allSpots);

// 4. Remaining services
ITicketRepository ticketRepository = new TicketRepository();
ITicketService ticketService = new TicketService(ticketRepository);
IPriceStrategy priceStrategy = new PerHourPricing();
IPaymentService paymentService = new PaymentService();

// 5. Orchestrator
IParkingLotService parkingLotService = new ParkingLotService(
    parkingStrategy, floorService, ticketService, priceStrategy, paymentService);

// 6. Composition root / public-facing facade
var entryGates = new List<EntryGate> { new EntryGate("E1", parkingLotService) };
var exitGates = new List<ExitGate> { new ExitGate("X1", parkingLotService) };

var parkingLot = new ParkingLot(floorService, parkingLotService, parkingStrategy, entryGates, exitGates);

parkingLot.Start();