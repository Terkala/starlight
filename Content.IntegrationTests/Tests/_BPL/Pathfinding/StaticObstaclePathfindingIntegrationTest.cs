#nullable enable
using System.Numerics;
using System.Threading.Tasks;
using Content.IntegrationTests.Fixtures;
using Content.Server._BPL.Pathfinding;
using Content.Server.Gravity;
using Content.Server.NPC.Components;
using Content.Server.NPC.Pathfinding;
using Content.Server.NPC.Systems;
using Content.Server.Physics.Controllers;
using Content.Shared.Gravity;
using Content.Shared.Movement.Components;
using Content.Shared.NPC;
using Content.Shared.Physics;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._BPL.Pathfinding;

/// <summary>
/// Arrival must hard-stop (no destination wiggle). Anchored machines must be full-tile nav blockers.
/// </summary>
[TestFixture]
[TestOf(typeof(NPCSteeringSystem))]
public sealed class StaticObstaclePathfindingIntegrationTest : GameTest
{
    private const float ArriveDistance = 1f;
    private const int MaxWalkTicks = 1200;
    private const int WalkTickStep = 10;
    private const int GraphSettleTicks = 45;

    public override PoolSettings PoolSettings => new()
    {
        Dirty = true,
        Connected = false,
    };

    [Test]
    public async Task StopsAtDestinationWithoutWiggle()
    {
        var pair = Pair;
        var server = pair.Server;
        var entMan = server.EntMan;

        var testMap = await pair.CreateTestMap();
        EntityUid walker = default;
        EntityCoordinates goal = default;

        await server.WaitAssertion(() =>
        {
            var grid = BuildOpenHallway(entMan, server.MapMan, testMap.MapId, width: 1);
            var start = new EntityCoordinates(grid, 0.5f, 1.5f);
            goal = new EntityCoordinates(grid, 6.5f, 1.5f);
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
        while (!arrived && ticks < MaxWalkTicks)
        {
            await server.WaitRunTicks(WalkTickStep);
            ticks += WalkTickStep;
            await server.WaitPost(() =>
            {
                if (!entMan.EntityExists(walker))
                    return;

                var xform = entMan.GetComponent<TransformComponent>(walker);
                if (xform.Coordinates.TryDistance(entMan, goal, out var dist) && dist <= ArriveDistance)
                    arrived = true;
            });
        }

        Assert.That(arrived, Is.True, $"NPC did not reach the destination in {ticks} ticks.");

        Vector2 settled = default;
        await server.WaitAssertion(() =>
        {
            settled = entMan.GetComponent<TransformComponent>(walker).Coordinates.Position;
            Assert.That(entMan.GetComponent<NPCSteeringComponent>(walker).Status,
                Is.EqualTo(SteeringStatus.InRange));
        });

        await server.WaitRunTicks(45);

        await server.WaitAssertion(() =>
        {
            var xform = entMan.GetComponent<TransformComponent>(walker);
            var moved = (xform.Coordinates.Position - settled).Length();
            Assert.That(moved, Is.LessThan(0.2f),
                $"NPC wiggled after arriving (drift={moved:0.00}).");

            var steering = entMan.GetComponent<NPCSteeringComponent>(walker);
            Assert.That(steering.Status, Is.EqualTo(SteeringStatus.InRange));

            var mover = entMan.GetComponent<InputMoverComponent>(walker);
            Assert.That(mover.CurTickSprintMovement.Length(), Is.LessThan(0.01f),
                "NPC was still applying movement input after arriving.");
        });
    }

    [Test]
    public async Task FinePathMarksCrewMonitorImpassable()
    {
        var pair = Pair;
        var server = pair.Server;
        var entMan = server.EntMan;
        var testMap = await pair.CreateTestMap();
        EntityCoordinates computerCoords = default;

        await server.WaitAssertion(() =>
        {
            var built = BuildMonitorHallway(entMan, server.MapMan, testMap.MapId);
            computerCoords = new EntityCoordinates(built.Grid, 3.5f, 2.5f);
        });

        await server.WaitRunTicks(GraphSettleTicks);

        await server.WaitAssertion(() =>
        {
            var path = entMan.System<PathfindingSystem>();
            var poly = path.GetPoly(computerCoords);
            Assert.That(poly, Is.Not.Null, "No path poly on the crew monitor tile.");
            var collides = ((CollisionGroup)poly!.Data.CollisionLayer & CollisionGroup.MobMask) != 0 ||
                           ((CollisionGroup)poly.Data.CollisionMask & CollisionGroup.MobLayer) != 0;
            Assert.That(collides, Is.True,
                "Crew monitor tile is still free space; A* will squeeze through the fixture gaps.");
        });
    }

    [Test]
    public async Task WalksAroundCrewMonitor()
    {
        var pair = Pair;
        var server = pair.Server;
        var entMan = server.EntMan;
        var testMap = await pair.CreateTestMap();
        EntityUid walker = default;
        EntityCoordinates goal = default;

        await server.WaitAssertion(() =>
        {
            var built = BuildMonitorHallway(entMan, server.MapMan, testMap.MapId);
            var start = new EntityCoordinates(built.Grid, 0.5f, 2.5f);
            goal = new EntityCoordinates(built.Grid, 6.5f, 2.5f);
            walker = SpawnWalker(entMan, start);
        });

        await server.WaitRunTicks(GraphSettleTicks);

        await server.WaitAssertion(() =>
        {
            var flags = entMan.System<PathfindingSystem>().GetFlags(walker);
            var steering = entMan.System<NPCSteeringSystem>().Register(walker, goal);
            steering.Flags = PathFlags.Interact | flags;
            steering.Range = ArriveDistance;
        });

        var arrived = false;
        var ticks = 0;
        var minY = float.MaxValue;
        var maxY = float.MinValue;
        var walkedThroughMonitor = false;
        while (!arrived && ticks < MaxWalkTicks)
        {
            await server.WaitRunTicks(WalkTickStep);
            ticks += WalkTickStep;
            await server.WaitPost(() =>
            {
                if (!entMan.EntityExists(walker))
                    return;

                var pos = entMan.GetComponent<TransformComponent>(walker).Coordinates.Position;
                minY = Math.Min(minY, pos.Y);
                maxY = Math.Max(maxY, pos.Y);
                if (pos.X is > 3.1f and < 3.9f && pos.Y is > 2.1f and < 2.9f)
                    walkedThroughMonitor = true;
                if (entMan.GetComponent<TransformComponent>(walker).Coordinates.TryDistance(entMan, goal, out var dist) &&
                    dist <= ArriveDistance)
                    arrived = true;
            });
        }

        Assert.That(arrived, Is.True,
            $"NPC did not walk around the crew monitor in {ticks} ticks.");
        Assert.That(walkedThroughMonitor, Is.False,
            "NPC walked through the crew monitor tile instead of around it.");
        Assert.That(maxY - minY, Is.GreaterThan(0.4f),
            "NPC stayed on the blocked center lane instead of pathing around the monitor.");
    }

    private static (Entity<MapGridComponent> Grid, EntityUid Computer) BuildMonitorHallway(
        IEntityManager entMan,
        IMapManager mapMan,
        MapId mapId)
    {
        var mapSys = entMan.System<SharedMapSystem>();
        var grid = mapMan.CreateGridEntity(mapId);
        var steel = new Tile(1);

        for (var x = 0; x <= 6; x++)
        {
            for (var y = 0; y <= 4; y++)
                mapSys.SetTile(grid, grid, new Vector2i(x, y), steel);
        }

        for (var x = 0; x <= 6; x++)
        {
            entMan.SpawnEntity("WallSolid", new EntityCoordinates(grid, x + 0.5f, 0.5f));
            entMan.SpawnEntity("WallSolid", new EntityCoordinates(grid, x + 0.5f, 4.5f));
        }

        var computer = entMan.SpawnEntity("ComputerCrewMonitoring", new EntityCoordinates(grid, 3.5f, 2.5f));
        entMan.EnsureComponent<GravityComponent>(grid.Owner);
        entMan.System<GravitySystem>().SetGravityEnabled(grid.Owner, true);
        entMan.System<PathRoomGraphSystem>().RebuildGrid(grid.Owner);
        return (grid, computer);
    }

    private static Entity<MapGridComponent> BuildOpenHallway(
        IEntityManager entMan,
        IMapManager mapMan,
        MapId mapId,
        int width)
    {
        var mapSys = entMan.System<SharedMapSystem>();
        var grid = mapMan.CreateGridEntity(mapId);
        var steel = new Tile(1);
        var maxY = width + 1;

        for (var x = 0; x <= 6; x++)
        {
            for (var y = 0; y <= maxY; y++)
                mapSys.SetTile(grid, grid, new Vector2i(x, y), steel);
        }

        for (var x = 0; x <= 6; x++)
        {
            entMan.SpawnEntity("WallSolid", new EntityCoordinates(grid, x + 0.5f, 0.5f));
            entMan.SpawnEntity("WallSolid", new EntityCoordinates(grid, x + 0.5f, maxY + 0.5f));
        }

        entMan.EnsureComponent<GravityComponent>(grid.Owner);
        entMan.System<GravitySystem>().SetGravityEnabled(grid.Owner, true);
        entMan.System<PathRoomGraphSystem>().RebuildGrid(grid.Owner);
        return grid;
    }

    private static EntityUid SpawnWalker(IEntityManager entMan, EntityCoordinates start)
    {
        var uid = entMan.SpawnEntity("MobCivilian", start);
        entMan.EnsureComponent<ActiveNPCComponent>(uid);
        entMan.System<MoverController>().EnsureActive(uid);
        return uid;
    }
}
