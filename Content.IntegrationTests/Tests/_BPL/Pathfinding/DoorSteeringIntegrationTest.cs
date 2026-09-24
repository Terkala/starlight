#nullable enable
using System.Numerics;
using Content.IntegrationTests.Fixtures;
using Content.Server._BPL.Pathfinding;
using Content.Server.Gravity;
using Content.Server.NPC.Components;
using Content.Server.NPC.Pathfinding;
using Content.Server.NPC.Systems;
using Content.Server.Physics.Controllers;
using Content.Shared.Doors.Components;
using Content.Shared.Gravity;
using Content.Shared.Movement.Components;
using Content.Shared.Movement.Pulling.Components;
using Content.Shared.Movement.Pulling.Systems;
using Content.Shared.NPC;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._BPL.Pathfinding;

/// <summary>
/// Door click-from-range, wait-without-wiggle, and resume after nudge/pull.
/// </summary>
[TestFixture]
[TestOf(typeof(NPCSteeringSystem))]
public sealed class DoorSteeringIntegrationTest : GameTest
{
    private const float ArriveDistance = 1.5f;
    private const int MaxWalkTicks = 900;
    private const int WalkTickStep = 5;
    private const int GraphSettleTicks = 30;

    [TestPrototypes]
    private const string Prototypes = @"
- type: entity
  id: BPLSteeringClickAirlock
  parent: Airlock
  components:
  - type: ApcPowerReceiver
    needsPower: false
  - type: AccessReader
    enabled: false
";

    public override PoolSettings PoolSettings => new()
    {
        Dirty = true,
        Connected = false,
    };

    [Test]
    public async Task ClicksDoorFromRangeThenCrosses()
    {
        var pair = Pair;
        var server = pair.Server;
        var entMan = server.EntMan;

        var testMap = await pair.CreateTestMap();
        EntityUid walker = default;
        EntityUid airlock = default;
        EntityCoordinates goal = default;
        var clickedFromRange = false;

        await server.WaitAssertion(() =>
        {
            var built = BuildDoorHallway(entMan, server.MapMan, testMap.MapId);
            airlock = built.Airlock;
            var start = new EntityCoordinates(built.Grid, 0.5f, 1.5f);
            goal = new EntityCoordinates(built.Grid, 4.5f, 1.5f);
            walker = SpawnWalker(entMan, start);
        });

        await server.WaitRunTicks(GraphSettleTicks);

        await server.WaitAssertion(() =>
        {
            var steering = entMan.System<NPCSteeringSystem>().Register(walker, goal);
            steering.Flags = PathFlags.Interact | entMan.System<PathfindingSystem>().GetFlags(walker);
            steering.Range = ArriveDistance;
        });

        var arrived = false;
        var ticks = 0;
        var lastX = 0f;
        DoorState lastDoor = DoorState.Closed;
        SteeringStatus lastStatus = SteeringStatus.Moving;
        var lastPath = 0;
        while (!arrived && ticks < MaxWalkTicks)
        {
            await server.WaitRunTicks(WalkTickStep);
            ticks += WalkTickStep;

            await server.WaitPost(() =>
            {
                if (!entMan.EntityExists(walker) || !entMan.EntityExists(airlock))
                    return;

                var door = entMan.GetComponent<DoorComponent>(airlock);
                lastDoor = door.State;
                if (door.State is DoorState.Opening or DoorState.Open)
                {
                    var x = entMan.GetComponent<TransformComponent>(walker).Coordinates.Position.X;
                    if (x < 2.0f)
                        clickedFromRange = true;
                }

                var xform = entMan.GetComponent<TransformComponent>(walker);
                lastX = xform.Coordinates.Position.X;
                if (entMan.TryGetComponent(walker, out NPCSteeringComponent? steering))
                {
                    lastStatus = steering.Status;
                    lastPath = steering.CurrentPath.Count;
                }

                if (xform.Coordinates.TryDistance(entMan, goal, out var dist) && dist <= ArriveDistance)
                    arrived = true;
            });
        }

        Assert.That(clickedFromRange, Is.True,
            "NPC should click-open the airlock from interaction range, not by standing on the door tile.");
        Assert.That(arrived, Is.True,
            $"NPC did not cross the airlock in {ticks} ticks (x={lastX:0.00}, door={lastDoor}, status={lastStatus}, path={lastPath}).");
    }

    [Test]
    public async Task WaitsStillWhileDoorOpens()
    {
        var pair = Pair;
        var server = pair.Server;
        var entMan = server.EntMan;

        var testMap = await pair.CreateTestMap();
        EntityUid walker = default;
        EntityUid airlock = default;
        EntityCoordinates goal = default;
        float? xAtOpening = null;
        float? xAfterWait = null;

        await server.WaitAssertion(() =>
        {
            var built = BuildDoorHallway(entMan, server.MapMan, testMap.MapId);
            airlock = built.Airlock;
            var start = new EntityCoordinates(built.Grid, 0.5f, 1.5f);
            goal = new EntityCoordinates(built.Grid, 4.5f, 1.5f);
            walker = SpawnWalker(entMan, start);
        });

        await server.WaitRunTicks(GraphSettleTicks);

        await server.WaitAssertion(() =>
        {
            var steering = entMan.System<NPCSteeringSystem>().Register(walker, goal);
            steering.Flags = PathFlags.Interact | entMan.System<PathfindingSystem>().GetFlags(walker);
            steering.Range = ArriveDistance;
        });

        var ticks = 0;
        while (xAtOpening == null && ticks < MaxWalkTicks)
        {
            await server.WaitRunTicks(1);
            ticks++;
            await server.WaitPost(() =>
            {
                if (!entMan.EntityExists(walker) || !entMan.EntityExists(airlock))
                    return;

                var door = entMan.GetComponent<DoorComponent>(airlock);
                if (door.State == DoorState.Opening)
                    xAtOpening = entMan.GetComponent<TransformComponent>(walker).Coordinates.Position.X;
            });
        }

        Assert.That(xAtOpening, Is.Not.Null, "Airlock never started opening.");

        await server.WaitRunTicks(8);

        await server.WaitAssertion(() =>
        {
            xAfterWait = entMan.GetComponent<TransformComponent>(walker).Coordinates.Position.X;
            var steering = entMan.GetComponent<NPCSteeringComponent>(walker);
            Assert.That(steering.Status, Is.Not.EqualTo(SteeringStatus.NoPath),
                "NPC gave up while waiting for the door.");
            Assert.That(Math.Abs(xAfterWait!.Value - xAtOpening!.Value), Is.LessThan(0.45f),
                $"NPC wiggled at the door (x {xAtOpening:0.00} -> {xAfterWait:0.00}) instead of waiting.");
        });
    }

    [Test]
    public async Task ResumesAfterNudge()
    {
        var pair = Pair;
        var server = pair.Server;
        var entMan = server.EntMan;

        var testMap = await pair.CreateTestMap();
        EntityUid walker = default;
        Entity<MapGridComponent> grid = default;
        EntityCoordinates goal = default;
        var nudged = false;

        await server.WaitAssertion(() =>
        {
            var built = BuildDoorHallway(entMan, server.MapMan, testMap.MapId);
            grid = built.Grid;
            var start = new EntityCoordinates(grid, 0.5f, 1.5f);
            goal = new EntityCoordinates(grid, 4.5f, 1.5f);
            walker = SpawnWalker(entMan, start);
        });

        await server.WaitRunTicks(GraphSettleTicks);

        await server.WaitAssertion(() =>
        {
            var steering = entMan.System<NPCSteeringSystem>().Register(walker, goal);
            steering.Flags = PathFlags.Interact | entMan.System<PathfindingSystem>().GetFlags(walker);
            steering.Range = ArriveDistance;
        });

        var arrived = false;
        var ticks = 0;
        var lastX = 0f;
        SteeringStatus lastStatus = SteeringStatus.Moving;
        var lastPath = 0;
        while (!arrived && ticks < MaxWalkTicks)
        {
            await server.WaitRunTicks(WalkTickStep);
            ticks += WalkTickStep;

            await server.WaitPost(() =>
            {
                if (!entMan.EntityExists(walker))
                    return;

                var xform = entMan.GetComponent<TransformComponent>(walker);
                lastX = xform.Coordinates.Position.X;
                if (entMan.TryGetComponent(walker, out NPCSteeringComponent? st))
                {
                    lastStatus = st.Status;
                    lastPath = st.CurrentPath.Count;
                }

                var x = lastX;
                if (!nudged && x > 1.2f && x < 2.4f)
                {
                    var physics = entMan.System<SharedPhysicsSystem>();
                    var xformSys = entMan.System<SharedTransformSystem>();
                    physics.SetLinearVelocity(walker, Vector2.Zero);
                    xformSys.SetCoordinates(walker, new EntityCoordinates(grid, 0.5f, 1.5f));
                    physics.WakeBody(walker);
                    nudged = true;
                }

                if (xform.Coordinates.TryDistance(entMan, goal, out var dist) && dist <= ArriveDistance)
                    arrived = true;
            });
        }

        Assert.That(nudged, Is.True, "NPC was never far enough along to nudge.");
        Assert.That(arrived, Is.True,
            $"NPC did not resume walking to the goal after being shoved back (x={lastX:0.00}, status={lastStatus}, path={lastPath}).");
        await server.WaitAssertion(() =>
        {
            var steering = entMan.GetComponent<NPCSteeringComponent>(walker);
            Assert.That(steering.Status, Is.Not.EqualTo(SteeringStatus.NoPath));
        });
    }

    [Test]
    public async Task ResumesAfterPull()
    {
        var pair = Pair;
        var server = pair.Server;
        var entMan = server.EntMan;

        var testMap = await pair.CreateTestMap();
        EntityUid walker = default;
        EntityUid puller = default;
        EntityUid airlock = default;
        EntityCoordinates goal = default;
        var pulled = false;

        await server.WaitAssertion(() =>
        {
            var built = BuildDoorHallway(entMan, server.MapMan, testMap.MapId);
            airlock = built.Airlock;
            var start = new EntityCoordinates(built.Grid, 0.5f, 1.5f);
            goal = new EntityCoordinates(built.Grid, 4.5f, 1.5f);
            walker = SpawnWalker(entMan, start);
            puller = entMan.SpawnEntity("MobCivilian", new EntityCoordinates(built.Grid, 0.5f, 1.5f));
        });

        await server.WaitRunTicks(GraphSettleTicks);

        await server.WaitAssertion(() =>
        {
            var steering = entMan.System<NPCSteeringSystem>().Register(walker, goal);
            steering.Flags = PathFlags.Interact | entMan.System<PathfindingSystem>().GetFlags(walker);
            steering.Range = ArriveDistance;
        });

        await server.WaitRunTicks(5);

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.System<PullingSystem>().TryStartPull(puller, walker), Is.True,
                "Failed to start pulling the pathing NPC.");
            pulled = true;
        });

        await server.WaitRunTicks(15);

        GameTick tickDuringPull = default;
        var lastX = 0f;
        SteeringStatus lastStatus = SteeringStatus.Moving;
        var lastPath = 0;
        var stillPulled = false;
        var canMove = false;
        await server.WaitAssertion(() =>
        {
            var steering = entMan.GetComponent<NPCSteeringComponent>(walker);
            Assert.That(steering.Status, Is.Not.EqualTo(SteeringStatus.NoPath),
                "NPC dropped its path while being pulled.");
            tickDuringPull = server.ResolveDependency<IGameTiming>().CurTick;
            Assert.That(entMan.System<PullingSystem>().TryStopPull(walker, entMan.GetComponent<PullableComponent>(walker)),
                Is.True);
            if (entMan.EntityExists(puller))
            {
                entMan.System<SharedTransformSystem>().SetCoordinates(
                    puller,
                    new EntityCoordinates(entMan.GetComponent<TransformComponent>(walker).ParentUid, 0.5f, 10.5f));
            }

            var physics = entMan.System<SharedPhysicsSystem>();
            physics.SetLinearVelocity(walker, Vector2.Zero);
            physics.WakeBody(walker);
            entMan.System<Content.Shared.ActionBlocker.ActionBlockerSystem>().UpdateCanMove(walker);
        });

        var arrived = false;
        var ticks = 0;
        DoorState lastDoor = DoorState.Closed;
        while (!arrived && ticks < MaxWalkTicks)
        {
            await server.WaitRunTicks(WalkTickStep);
            ticks += WalkTickStep;
            await server.WaitPost(() =>
            {
                if (!entMan.EntityExists(walker))
                    return;
                var xform = entMan.GetComponent<TransformComponent>(walker);
                lastX = xform.Coordinates.Position.X;
                if (entMan.TryGetComponent(walker, out NPCSteeringComponent? st))
                {
                    lastStatus = st.Status;
                    lastPath = st.CurrentPath.Count;
                }

                stillPulled = entMan.System<PullingSystem>().IsPulled(walker);
                canMove = entMan.TryGetComponent(walker, out InputMoverComponent? mover) && mover.CanMove;
                if (entMan.TryGetComponent(airlock, out DoorComponent? door))
                    lastDoor = door.State;
                if (xform.Coordinates.TryDistance(entMan, goal, out var dist) && dist <= ArriveDistance)
                    arrived = true;
            });
        }

        Assert.That(pulled, Is.True);
        Assert.That(arrived, Is.True,
            $"NPC did not resume after being released from a pull (x={lastX:0.00}, door={lastDoor}, status={lastStatus}, path={lastPath}, pulled={stillPulled}, canMove={canMove}, ticks={ticks}).");
        await server.WaitAssertion(() =>
        {
            var timing = server.ResolveDependency<IGameTiming>();
            Assert.That(timing.CurTick.Value, Is.GreaterThan(tickDuringPull.Value));
        });
    }

    private readonly record struct DoorHallway(Entity<MapGridComponent> Grid, EntityUid Airlock);

    private static DoorHallway BuildDoorHallway(
        IEntityManager entMan,
        IMapManager mapMan,
        MapId mapId)
    {
        var mapSys = entMan.System<SharedMapSystem>();
        var grid = mapMan.CreateGridEntity(mapId);
        var steel = new Tile(1);

        for (var x = 0; x <= 4; x++)
        {
            for (var y = 0; y <= 2; y++)
                mapSys.SetTile(grid, grid, new Vector2i(x, y), steel);
        }

        for (var x = 0; x <= 4; x++)
        {
            if (x == 2)
                continue;
            entMan.SpawnEntity("WallSolid", new EntityCoordinates(grid, x + 0.5f, 0.5f));
            entMan.SpawnEntity("WallSolid", new EntityCoordinates(grid, x + 0.5f, 2.5f));
        }

        entMan.SpawnEntity("WallSolid", new EntityCoordinates(grid, 2.5f, 0.5f));
        entMan.SpawnEntity("WallSolid", new EntityCoordinates(grid, 2.5f, 2.5f));
        var airlock = entMan.SpawnEntity("BPLSteeringClickAirlock", new EntityCoordinates(grid, 2.5f, 1.5f));

        entMan.EnsureComponent<GravityComponent>(grid.Owner);
        entMan.System<GravitySystem>().SetGravityEnabled(grid.Owner, true);
        entMan.System<PathRoomGraphSystem>().RebuildGrid(grid.Owner);
        return new DoorHallway(grid, airlock);
    }

    private static EntityUid SpawnWalker(IEntityManager entMan, EntityCoordinates start)
    {
        var uid = entMan.SpawnEntity("MobCivilian", start);
        entMan.EnsureComponent<ActiveNPCComponent>(uid);
        entMan.System<MoverController>().EnsureActive(uid);
        return uid;
    }
}
