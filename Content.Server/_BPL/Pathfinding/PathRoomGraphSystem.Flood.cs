using System.Linq;
using System.Numerics;
using Content.Shared.Doors.Components;
using Content.Shared.Physics;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;

namespace Content.Server._BPL.Pathfinding;

public sealed partial class PathRoomGraphSystem
{
    private static readonly Vector2i[] NeighborOffsets =
    {
        new(1, 0),
        new(-1, 0),
        new(0, 1),
        new(0, -1),
    };

    private readonly Vector2i[] _neighborOffsets = NeighborOffsets;

    private enum TileClass : byte
    {
        Space,
        Wall,
        Door,
        Floor,
    }

    public void RebuildGrid(EntityUid gridUid)
    {
        if (!TryComp(gridUid, out MapGridComponent? mapGrid))
            return;

        var graph = EnsureComp<PathRoomGraphComponent>(gridUid);
        graph.Rooms.Clear();
        graph.Edges.Clear();
        graph.TileToRoom.Clear();
        graph.DoorToEdges.Clear();
        graph.StaleRooms.Clear();
        graph.PendingDirtyTiles.Clear();
        graph.NextRoomId = 0;
        graph.NextEdgeId = 0;
        graph.StructuralRevision++;
        PathfindingMetrics.CacheInvalidations.WithLabels("graph").Inc();
        PathfindingMetrics.RoomRescans.Inc();

        var classes = new Dictionary<Vector2i, TileClass>();
        var doorTiles = new Dictionary<Vector2i, EntityUid>();
        var climbDoors = new HashSet<EntityUid>();

        foreach (var tileRef in _maps.GetAllTiles(gridUid, mapGrid))
        {
            var tile = tileRef.GridIndices;
            ClassifyTile(gridUid, mapGrid, tile, tileRef, classes, doorTiles, climbDoors);
        }

        var visited = new HashSet<Vector2i>();
        foreach (var (tile, kind) in classes)
        {
            if (kind != TileClass.Floor || !visited.Add(tile))
                continue;

            FloodRoom(graph, classes, visited, tile);
        }

        BuildDoorEdges(gridUid, mapGrid, graph, classes, doorTiles, climbDoors);
        StructuralInvalidated?.Invoke(gridUid);
    }

    private void ClassifyTile(
        EntityUid gridUid,
        MapGridComponent mapGrid,
        Vector2i tile,
        TileRef tileRef,
        Dictionary<Vector2i, TileClass> classes,
        Dictionary<Vector2i, EntityUid> doorTiles,
        HashSet<EntityUid> climbDoors)
    {
        if (tileRef.Tile.IsEmpty)
        {
            classes[tile] = TileClass.Space;
            return;
        }

        EntityUid? door = null;
        var wall = false;
        var climbable = false;

        foreach (var ent in _maps.GetAnchoredEntities(gridUid, mapGrid, tile))
        {
            if (_climbableQuery.HasComponent(ent))
                climbable = true;

            if (!_fixturesQuery.TryGetComponent(ent, out var fixtures))
                continue;

            if (_doorQuery.HasComponent(ent) && IsBodyRelevant(fixtures))
            {
                door = ent;
                continue;
            }

            // Walls/windows are Impassable. Crates, lockers, and mobs are local obstructions only.
            if (HasStructuralCollision(fixtures))
            {
                wall = true;
                break;
            }
        }

        if (wall)
        {
            classes[tile] = TileClass.Wall;
            return;
        }

        if (door != null)
        {
            classes[tile] = TileClass.Door;
            doorTiles[tile] = door.Value;
            if (climbable)
                climbDoors.Add(door.Value);
            return;
        }

        classes[tile] = TileClass.Floor;
    }

    private static void FloodRoom(
        PathRoomGraphComponent graph,
        Dictionary<Vector2i, TileClass> classes,
        HashSet<Vector2i> visited,
        Vector2i seed)
    {
        var room = new PathRoom
        {
            LocalId = graph.NextRoomId++,
            SeedTile = seed,
            LastScan = TimeSpan.Zero,
        };

        var queue = new Queue<Vector2i>();
        queue.Enqueue(seed);
        graph.TileToRoom[seed] = room.LocalId;

        while (queue.Count > 0)
        {
            var tile = queue.Dequeue();
            room.TileCount++;

            foreach (var offset in NeighborOffsets)
            {
                var next = tile + offset;
                if (!classes.TryGetValue(next, out var kind))
                    continue;

                if (kind == TileClass.Door)
                {
                    continue;
                }

                if (kind != TileClass.Floor || !visited.Add(next))
                    continue;

                graph.TileToRoom[next] = room.LocalId;
                queue.Enqueue(next);
            }
        }

        room.Signature = (ulong) room.TileCount * 397 ^ (ulong) (uint) room.SeedTile.GetHashCode();
        graph.Rooms[room.LocalId] = room;
    }

    private void BuildDoorEdges(
        EntityUid gridUid,
        MapGridComponent mapGrid,
        PathRoomGraphComponent graph,
        Dictionary<Vector2i, TileClass> classes,
        Dictionary<Vector2i, EntityUid> doorTiles,
        HashSet<EntityUid> climbDoors)
    {
        var doors = new Dictionary<EntityUid, List<Vector2i>>();
        foreach (var (tile, door) in doorTiles)
        {
            if (!doors.TryGetValue(door, out var list))
            {
                list = new List<Vector2i>();
                doors[door] = list;
            }

            list.Add(tile);
            if (graph.TileToRoom.TryGetValue(tile, out _))
                continue;
        }

        foreach (var (door, tiles) in doors)
        {
            var rooms = new Dictionary<int, Vector2i>();
            foreach (var tile in tiles)
            {
                foreach (var offset in NeighborOffsets)
                {
                    var next = tile + offset;
                    if (!graph.TileToRoom.TryGetValue(next, out var roomId))
                        continue;

                    rooms.TryAdd(roomId, next);
                    if (graph.Rooms.TryGetValue(roomId, out var room))
                        room.Doors.Add(door);
                }
            }

            if (rooms.Count < 2)
                continue;

            var roomIds = rooms.Keys.ToArray();
            for (var i = 0; i < roomIds.Length; i++)
            {
                for (var j = i + 1; j < roomIds.Length; j++)
                {
                    var a = roomIds[i];
                    var b = roomIds[j];
                    var edge = new PathRoomEdge
                    {
                        EdgeId = graph.NextEdgeId++,
                        Door = door,
                        RoomA = a,
                        RoomB = b,
                        MouthA = rooms[a] + new Vector2(0.5f, 0.5f),
                        MouthB = rooms[b] + new Vector2(0.5f, 0.5f),
                        SemanticRevision = GetDoorRevision(door),
                        RequiresClimb = climbDoors.Contains(door),
                    };

                    graph.Edges[edge.EdgeId] = edge;
                    graph.Rooms[a].EdgeIds.Add(edge.EdgeId);
                    graph.Rooms[b].EdgeIds.Add(edge.EdgeId);

                    if (!graph.DoorToEdges.TryGetValue(door, out var edgeList))
                    {
                        edgeList = new List<int>();
                        graph.DoorToEdges[door] = edgeList;
                    }

                    edgeList.Add(edge.EdgeId);
                }
            }
        }

        AttachPathPortals(gridUid, mapGrid, graph);
    }

    private void AttachPathPortals(EntityUid gridUid, MapGridComponent mapGrid, PathRoomGraphComponent graph)
    {
        if (!TryComp<Content.Server.NPC.Pathfinding.GridPathfindingComponent>(gridUid, out var pathfinding))
            return;

        foreach (var (portal, _) in pathfinding.PortalLookup)
        {
            var tileA = _maps.CoordinatesToTile(gridUid, mapGrid, portal.CoordinatesA);
            var gridB = _transform.GetGrid(portal.CoordinatesB);
            if (gridB != gridUid)
            {
                // Cross-grid portal: connect room A to a synthetic extra edge stored on this graph.
                if (!graph.TileToRoom.TryGetValue(tileA, out var roomA))
                    continue;

                var edge = new PathRoomEdge
                {
                    EdgeId = graph.NextEdgeId++,
                    Door = null,
                    RoomA = roomA,
                    RoomB = roomA,
                    MouthA = portal.CoordinatesA.Position,
                    MouthB = portal.CoordinatesB.Position,
                    IsPortal = true,
                    PortalHandle = portal.Handle,
                };
                graph.Edges[edge.EdgeId] = edge;
                graph.Rooms[roomA].EdgeIds.Add(edge.EdgeId);
                continue;
            }

            var tileB = _maps.CoordinatesToTile(gridUid, mapGrid, portal.CoordinatesB);
            if (!graph.TileToRoom.TryGetValue(tileA, out var a) ||
                !graph.TileToRoom.TryGetValue(tileB, out var b) ||
                a == b)
            {
                continue;
            }

            var portalEdge = new PathRoomEdge
            {
                EdgeId = graph.NextEdgeId++,
                Door = null,
                RoomA = a,
                RoomB = b,
                MouthA = portal.CoordinatesA.Position,
                MouthB = portal.CoordinatesB.Position,
                IsPortal = true,
                PortalHandle = portal.Handle,
            };
            graph.Edges[portalEdge.EdgeId] = portalEdge;
            graph.Rooms[a].EdgeIds.Add(portalEdge.EdgeId);
            graph.Rooms[b].EdgeIds.Add(portalEdge.EdgeId);
        }
    }

    private static bool HasStructuralCollision(FixturesComponent fixtures)
    {
        const int structural = (int) CollisionGroup.Impassable;
        foreach (var fixture in fixtures.Fixtures.Values)
        {
            if (!fixture.Hard)
                continue;

            if ((fixture.CollisionLayer & structural) != 0x0)
                return true;
        }

        return false;
    }

    private static bool IsBodyRelevant(FixturesComponent fixtures)
    {
        const int mask = (int) CollisionGroup.MobMask;
        const int layer = (int) CollisionGroup.MobLayer;

        foreach (var fixture in fixtures.Fixtures.Values)
        {
            if (!fixture.Hard)
                continue;

            if ((fixture.CollisionMask & layer) != 0x0 ||
                (fixture.CollisionLayer & mask) != 0x0)
            {
                return true;
            }
        }

        return false;
    }
}
