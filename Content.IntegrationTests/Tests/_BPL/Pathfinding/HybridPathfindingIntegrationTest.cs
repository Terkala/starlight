#nullable enable
using Content.IntegrationTests.Fixtures;
using Content.Server._BPL.Pathfinding;
using Content.Shared.Doors.Components;
using Content.Shared.Doors.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._BPL.Pathfinding;

[TestFixture]
[TestOf(typeof(PathRoomGraphSystem))]
public sealed class HybridPathfindingIntegrationTest : GameTest
{
    [TestPrototypes]
    private const string Prototypes = @"
- type: entity
  id: BPLPathfindAirlockDummy
  parent: Airlock
  components:
  - type: ApcPowerReceiver
    needsPower: false
  - type: AccessReader
    access: [[""Command""]]
- type: entity
  id: BPLPathfindBoltDoorDummy
  components:
  - type: Transform
  - type: Physics
    bodyType: Static
  - type: Fixtures
    fixtures:
      fix1:
        shape:
          !type:PhysShapeAabb
            bounds: ""-0.49,-0.49,0.49,0.49""
        layer:
        - Impassable
  - type: Door
  - type: DoorBolt
";

    [Test]
    public async Task TwoRoomsShareOneDoorEdge()
    {
        var pair = Pair;
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();
        var entMan = server.EntMan;
        var mapMan = server.MapMan;
        var mapSys = entMan.System<SharedMapSystem>();

        Entity<MapGridComponent> grid = default;
        EntityUid airlock = default;

        await server.WaitAssertion(() =>
        {
            grid = mapMan.CreateGridEntity(testMap.MapId);
            var steel = new Tile(1);
            for (var x = 0; x <= 4; x++)
            {
                for (var y = 0; y <= 2; y++)
                    mapSys.SetTile(grid, grid, new Vector2i(x, y), steel);
            }

            entMan.SpawnEntity("WallSolid", new EntityCoordinates(grid, 2.5f, 0.5f));
            entMan.SpawnEntity("WallSolid", new EntityCoordinates(grid, 2.5f, 2.5f));
            airlock = entMan.SpawnEntity("BPLPathfindAirlockDummy", new EntityCoordinates(grid, 2.5f, 1.5f));
        });

        await server.WaitRunTicks(5);

        await server.WaitAssertion(() =>
        {
            var rooms = entMan.System<PathRoomGraphSystem>();
            rooms.RebuildGrid(grid.Owner);
            Assert.That(rooms.GetRoomCount(grid.Owner), Is.GreaterThanOrEqualTo(2));
            Assert.That(rooms.HasDoorEdge(grid.Owner, airlock), Is.True);
        });
    }

    [Test]
    public async Task BoltBumpsDoorRevision()
    {
        var pair = Pair;
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();
        var entMan = server.EntMan;

        EntityUid airlock = default;
        uint before = 0;

        await server.WaitAssertion(() =>
        {
            airlock = entMan.SpawnEntity("BPLPathfindBoltDoorDummy", testMap.GridCoords);
            var revSys = entMan.System<DoorSemanticRevisionSystem>();
            revSys.Refresh(airlock);
            before = entMan.GetComponent<DoorPathRevisionComponent>(airlock).SemanticRevision;

            var bolts = entMan.GetComponent<DoorBoltComponent>(airlock);
            Assert.That(entMan.System<SharedDoorSystem>().TrySetBoltDown((airlock, bolts), true), Is.True);
        });

        await server.WaitRunTicks(1);

        await server.WaitAssertion(() =>
        {
            var after = entMan.GetComponent<DoorPathRevisionComponent>(airlock).SemanticRevision;
            Assert.That(after, Is.GreaterThan(before));
            Assert.That(entMan.GetComponent<DoorPathRevisionComponent>(airlock).Snapshot.Bolted, Is.True);
        });
    }

    [Test]
    public async Task OpeningDoorDoesNotMergeRooms()
    {
        var pair = Pair;
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();
        var entMan = server.EntMan;
        var mapMan = server.MapMan;
        var mapSys = entMan.System<SharedMapSystem>();
        var doors = entMan.System<SharedDoorSystem>();

        Entity<MapGridComponent> grid = default;
        EntityUid airlock = default;
        int roomsBefore = 0;

        await server.WaitAssertion(() =>
        {
            grid = mapMan.CreateGridEntity(testMap.MapId);
            var steel = new Tile(1);
            for (var x = 0; x <= 4; x++)
            {
                for (var y = 0; y <= 2; y++)
                    mapSys.SetTile(grid, grid, new Vector2i(x, y), steel);
            }

            entMan.SpawnEntity("WallSolid", new EntityCoordinates(grid, 2.5f, 0.5f));
            entMan.SpawnEntity("WallSolid", new EntityCoordinates(grid, 2.5f, 2.5f));
            airlock = entMan.SpawnEntity("BPLPathfindAirlockDummy", new EntityCoordinates(grid, 2.5f, 1.5f));
        });

        await server.WaitRunTicks(5);

        await server.WaitAssertion(() =>
        {
            entMan.System<PathRoomGraphSystem>().RebuildGrid(grid.Owner);
            roomsBefore = entMan.System<PathRoomGraphSystem>().GetRoomCount(grid.Owner);
            doors.StartOpening(airlock);
        });

        await server.WaitRunTicks(20);

        await server.WaitAssertion(() =>
        {
            var roomsAfter = entMan.System<PathRoomGraphSystem>().GetRoomCount(grid.Owner);
            Assert.That(roomsAfter, Is.EqualTo(roomsBefore));
            Assert.That(roomsAfter, Is.GreaterThanOrEqualTo(2));
        });
    }

    [Test]
    public async Task LivePredicateMatchesAccessOnSpawnedAirlock()
    {
        var pair = Pair;
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();
        var entMan = server.EntMan;

        await server.WaitAssertion(() =>
        {
            var airlock = entMan.SpawnEntity("BPLPathfindAirlockDummy", testMap.GridCoords);
            var revSys = entMan.System<DoorSemanticRevisionSystem>();
            revSys.Refresh(airlock);
            var snap = entMan.GetComponent<DoorPathRevisionComponent>(airlock).Snapshot;

            var allowed = PathAccessProfile.Create(
                new[] { new Robust.Shared.Prototypes.ProtoId<Content.Shared.Access.AccessLevelPrototype>("Command") },
                Array.Empty<ulong>(),
                Content.Server.NPC.Pathfinding.PathFlags.Interact,
                0,
                0);
            var denied = PathAccessProfile.Create(
                Array.Empty<Robust.Shared.Prototypes.ProtoId<Content.Shared.Access.AccessLevelPrototype>>(),
                Array.Empty<ulong>(),
                Content.Server.NPC.Pathfinding.PathFlags.None,
                0,
                0);

            Assert.That(DoorTraversalSystem.AccessAllowed(allowed, snap), Is.True);
            Assert.That(DoorTraversalSystem.EvaluateSnapshot(denied, snap, 0f, true).IsAllowed, Is.False);
        });
    }
}
