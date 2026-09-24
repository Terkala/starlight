using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.NPC.Pathfinding;
using Content.Shared.Climbing.Components;
using Content.Shared.Physics;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;

namespace Content.Server._BPL.Pathfinding;

public sealed partial class PathBrokerSystem
{
    private readonly record struct NextDoor(EntityUid Door, EntityCoordinates Mouth, uint Revision, int FarRoom);

    private bool TrySameRoomLos(EntityUid agent, EntityCoordinates goal)
    {
        if (!_physicsQuery.TryGetComponent(agent, out var physics))
            return false;

        // Interaction rays ignore MidImpassable furniture. Standing AIs still collide with tables,
        // so a "clear" LOS here would skip FinePath and walk Euclidean into the table.
        if (ClimbablesBlockStandingWalk(agent, goal))
            return false;

        return _interaction.InRangeUnobstructed(
            agent,
            goal,
            range: 30f,
            collisionMask: (CollisionGroup) physics.CollisionMask | CollisionGroup.TableLayer);
    }

    private bool ClimbablesBlockStandingWalk(EntityUid agent, EntityCoordinates goal)
    {
        if (_climbingQuery.TryGetComponent(agent, out var climbing) && climbing.IsClimbing)
            return false;

        if (_knockedDownQuery.HasComponent(agent))
            return false;

        var startXform = Transform(agent);
        if (startXform.GridUid is not { } grid || !TryComp(grid, out MapGridComponent? mapGrid))
            return false;

        if (_transform.GetGrid(goal) != grid)
            return false;

        var startTile = _maps.CoordinatesToTile(grid, mapGrid, startXform.Coordinates);
        var goalTile = _maps.CoordinatesToTile(grid, mapGrid, goal);
        var minX = Math.Min(startTile.X, goalTile.X);
        var maxX = Math.Max(startTile.X, goalTile.X);
        var minY = Math.Min(startTile.Y, goalTile.Y);
        var maxY = Math.Max(startTile.Y, goalTile.Y);

        for (var x = minX; x <= maxX; x++)
        {
            for (var y = minY; y <= maxY; y++)
            {
                foreach (var ent in _maps.GetAnchoredEntities(grid, mapGrid, new Vector2i(x, y)))
                {
                    if (_climbableQuery.HasComponent(ent))
                        return true;
                }
            }
        }

        return false;
    }

    private async Task<PathResultEvent> FinePath(
        EntityUid agent,
        EntityCoordinates start,
        EntityCoordinates end,
        float range,
        PathFlags flags,
        PathAccessProfile profile,
        int? allowedRoom,
        EntityUid? terminalDoor,
        CancellationToken cancel,
        int? allowedRoom2 = null)
    {
        // Room limits and live tile costs stay inside this broker. The hop itself uses
        // Starlight's pathfinder unchanged.
        _ = allowedRoom;
        _ = terminalDoor;
        _ = profile;
        _ = allowedRoom2;
        var request = _pathfinding.EnqueueOwnedRequest(_fineRequests, agent, start, end, range, cancel, flags);
        var result = await request.Task;
        // Same context as PathfindingSystem.GetPath: the task is already complete.
#pragma warning disable RA0004
        return new PathResultEvent(result, request.Polys);
#pragma warning restore RA0004
    }

    private HybridPathResult FinishHop(
        HybridPathResult result,
        NPCHybridPathComponent hybrid,
        PathResultEvent hop,
        EntityUid door,
        PathRoomId targetRoom)
    {
        result.CommittedDoor = door;
        result.GoalRoom = targetRoom;
        result.Path = hop.Path;

        if (IsUsableHop(hop.Result))
        {
            result.Result = PathResult.PartialPath;
            PathfindingMetrics.BrokerResults.WithLabels("partial").Inc();
        }
        else
        {
            result.Result = hop.Result;
            PathfindingMetrics.BrokerResults.WithLabels(Label(hop.Result)).Inc();
        }

        return result;
    }

    private bool TryReuseCommittedDoor(
        EntityUid agent,
        NPCHybridPathComponent hybrid,
        PathRoomId current,
        PathAccessProfile profile,
        out EntityUid door,
        out EntityCoordinates mouth,
        out int farRoom)
    {
        door = default;
        mouth = default;
        farRoom = 0;
        if (hybrid.CommittedDoor is not { } committed)
            return false;

        if (IsDoorPenalized(hybrid, committed))
            return false;

        if (_rooms.GetDoorRevision(committed) != hybrid.CommittedDoorRevision)
            return false;

        var verdict = _traversal.EvaluateDoorEdge(profile, committed, _rooms.DoorRequiresClimb(current.GridUid, committed));
        if (!verdict.IsAllowed)
            return false;

        var far = _rooms.GetDoorFarRoom(current.GridUid, committed, current);
        if (far is null)
            return false;

        var mouthPos = _rooms.GetDoorMouth(far.Value, committed);
        if (mouthPos is null)
            return false;

        if (hybrid.CommittedFarRoom is { } committedFar)
        {
            if (current.LocalId == committedFar)
                return false;

            var committedFarRoom = new PathRoomId(current.GridUid, committedFar);
            mouthPos = _rooms.GetDoorMouth(committedFarRoom, committed) ?? mouthPos;
            far = committedFarRoom;
        }

        door = committed;
        mouth = new EntityCoordinates(current.GridUid, mouthPos.Value);
        farRoom = far.Value.LocalId;
        return true;
    }

    private NextDoor? FindNextDoor(
        EntityUid agent,
        PathRoomId current,
        PathRoomId goal,
        PathAccessProfile profile,
        NPCHybridPathComponent hybrid)
    {
        if (!_rooms.TryGetGraph(current.GridUid, out var graph))
            return null;

        var key = new CoarseBatchKey(current.GridUid, goal.LocalId, profile, graph.StructuralRevision);
        var fanIn = CountFanIn(key);
        List<int>? edgePath;

        if (fanIn >= 2)
        {
            EnqueueCoarse(key, hybrid.Priority);
            PathfindingMetrics.CoarseBatchSize.Observe(fanIn);
            var tree = GetOrBuildReverseTree(key, graph, profile);
            if (!tree.TryGetValue(current.LocalId, out var step))
                return null;

            edgePath = new List<int> { step.EdgeId };
        }
        else
        {
            edgePath = CoarseAStar(graph, current.LocalId, goal.LocalId, profile, hybrid);
        }

        if (edgePath is null || edgePath.Count == 0)
            return null;

        if (!graph.Edges.TryGetValue(edgePath[0], out var edge) || edge.Door is not { } door)
            return null;

        if (IsDoorPenalized(hybrid, door))
            return null;

        var verdict = _traversal.EvaluateDoorEdge(profile, door, edge.RequiresClimb);
        if (!verdict.IsAllowed)
            return null;

        var farMouthLocal = edge.RoomA == current.LocalId ? edge.MouthB : edge.MouthA;
        var farRoom = edge.RoomA == current.LocalId ? edge.RoomB : edge.RoomA;
        var mouth = new EntityCoordinates(current.GridUid, farMouthLocal);
        return new NextDoor(door, mouth, _rooms.GetDoorRevision(door), farRoom);
    }

    private List<int>? CoarseAStar(
        PathRoomGraphComponent graph,
        int start,
        int goal,
        PathAccessProfile profile,
        NPCHybridPathComponent hybrid)
    {
        var adjacency = BuildAdjacency(graph);
        return CoarseGraphSearch.AStar(
            start,
            goal,
            adjacency,
            id => EdgeCost(graph, id, profile, hybrid),
            (_, _) => 0f);
    }

    private Dictionary<int, (int ParentRoom, int EdgeId, float Cost)> GetOrBuildReverseTree(
        CoarseBatchKey key,
        PathRoomGraphComponent graph,
        PathAccessProfile profile)
    {
        if (_reverseCache.TryGetValue(key, out var cached))
        {
            PathfindingMetrics.CacheHits.WithLabels("dijkstra").Inc();
            return cached.Tree;
        }

        var adjacency = BuildAdjacency(graph);
        var tree = CoarseGraphSearch.ReverseDijkstra(
            key.GoalRoom,
            adjacency,
            id => EdgeCost(graph, id, profile, null));

        var doors = new HashSet<EntityUid>();
        foreach (var step in tree.Values)
        {
            if (graph.Edges.TryGetValue(step.EdgeId, out var edge) && edge.Door is { } door)
                doors.Add(door);
        }

        _reverseCache[key] = new ReverseTreeCache(tree, doors);
        return tree;
    }

    private Dictionary<int, int> GetComponents(EntityUid grid, PathAccessProfile profile)
    {
        if (!_rooms.TryGetGraph(grid, out var graph))
            return new Dictionary<int, int>();

        var key = new CoarseBatchKey(grid, 0, profile, graph.StructuralRevision);
        if (_components.TryGetValue(key, out var cached))
        {
            PathfindingMetrics.CacheHits.WithLabels("components").Inc();
            return cached.Components;
        }

        var adjacency = BuildAdjacency(graph);
        var rooms = graph.Rooms.Keys.ToArray();
        var components = CoarseGraphSearch.ConnectedComponents(
            rooms,
            adjacency,
            id => EdgeCost(graph, id, profile, null));

        var doors = new HashSet<EntityUid>();
        foreach (var edge in graph.Edges.Values)
        {
            if (edge.Door is { } door)
                doors.Add(door);
        }

        _components[key] = new ComponentCache(components, doors);
        return components;
    }

    private void EnqueueCoarse(CoarseBatchKey key, PathBrokerPriority priority)
    {
        if (!_queuedCoarse.Add(key))
            return;

        _coarseQueue.Enqueue(priority, key);
    }

    private static Dictionary<int, List<CoarseGraphSearch.Edge>> BuildAdjacency(PathRoomGraphComponent graph)
    {
        var adjacency = new Dictionary<int, List<CoarseGraphSearch.Edge>>();
        foreach (var room in graph.Rooms.Keys)
        {
            adjacency[room] = new List<CoarseGraphSearch.Edge>();
        }

        foreach (var edge in graph.Edges.Values)
        {
            if (edge.IsPortal && edge.RoomA == edge.RoomB)
                continue;

            var item = new CoarseGraphSearch.Edge(edge.EdgeId, edge.RoomA, edge.RoomB, 1f);
            if (adjacency.TryGetValue(edge.RoomA, out var a))
                a.Add(item);
            if (edge.RoomA != edge.RoomB && adjacency.TryGetValue(edge.RoomB, out var b))
                b.Add(item);
        }

        return adjacency;
    }

    private float? EdgeCost(
        PathRoomGraphComponent graph,
        int edgeId,
        PathAccessProfile profile,
        NPCHybridPathComponent? hybrid)
    {
        if (!graph.Edges.TryGetValue(edgeId, out var edge))
            return null;

        if (edge.IsPortal)
            return 1f;

        if (edge.Door is not { } door)
            return 1f;

        if (hybrid != null && IsDoorPenalized(hybrid, door))
            return 1000f;

        var verdict = _traversal.EvaluateDoorEdge(profile, door, edge.RequiresClimb);
        if (!verdict.IsAllowed)
            return null;

        return verdict.CostMultiplier;
    }

    private int CountFanIn(CoarseBatchKey key)
    {
        var count = 0;
        var query = EntityQueryEnumerator<NPCHybridPathComponent>();
        while (query.MoveNext(out _, out var hybrid))
        {
            if (hybrid.GoalRoom is not { } goal)
                continue;

            if (goal.GridUid != key.Grid || goal.LocalId != key.GoalRoom)
                continue;

            if (hybrid.Profile is null || !hybrid.Profile.Equals(key.Profile))
                continue;

            count++;
        }

        return count;
    }

    private void OnDoorEdgeInvalidated(EntityUid grid, EntityUid door)
    {
        var reverseRemove = new List<CoarseBatchKey>();
        foreach (var (key, cache) in _reverseCache)
        {
            if (key.Grid == grid && cache.Doors.Contains(door))
                reverseRemove.Add(key);
        }

        foreach (var key in reverseRemove)
        {
            _reverseCache.Remove(key);
            PathfindingMetrics.CacheInvalidations.WithLabels("edge").Inc();
        }

        var compRemove = new List<CoarseBatchKey>();
        foreach (var (key, cache) in _components)
        {
            if (key.Grid == grid && cache.Doors.Contains(door))
                compRemove.Add(key);
        }

        foreach (var key in compRemove)
        {
            _components.Remove(key);
            PathfindingMetrics.CacheInvalidations.WithLabels("edge").Inc();
        }
    }

    private void OnStructuralInvalidated(EntityUid grid)
    {
        var reverse = _reverseCache.Keys.Where(k => k.Grid == grid).ToList();
        foreach (var key in reverse)
        {
            _reverseCache.Remove(key);
        }

        var comps = _components.Keys.Where(k => k.Grid == grid).ToList();
        foreach (var key in comps)
        {
            _components.Remove(key);
        }

        PathfindingMetrics.CacheInvalidations.WithLabels("graph").Inc();
    }

    private void ExpirePenalties(NPCHybridPathComponent hybrid)
    {
        var now = _timing.CurTime;
        var remove = new List<EntityUid>();
        foreach (var (door, expiry) in hybrid.DoorPenalties)
        {
            if (expiry <= now)
                remove.Add(door);
        }

        foreach (var door in remove)
        {
            hybrid.DoorPenalties.Remove(door);
        }
    }

    private bool IsDoorPenalized(NPCHybridPathComponent hybrid, EntityUid door)
    {
        return hybrid.DoorPenalties.TryGetValue(door, out var expiry) && expiry > _timing.CurTime;
    }

    private void PenalizeDoor(NPCHybridPathComponent hybrid, EntityUid door)
    {
        hybrid.DoorPenalties[door] = _timing.CurTime + TimeSpan.FromSeconds(3);
    }

    private sealed class ReverseTreeCache
    {
        public readonly Dictionary<int, (int ParentRoom, int EdgeId, float Cost)> Tree;
        public readonly HashSet<EntityUid> Doors;

        public ReverseTreeCache(
            Dictionary<int, (int ParentRoom, int EdgeId, float Cost)> tree,
            HashSet<EntityUid> doors)
        {
            Tree = tree;
            Doors = doors;
        }
    }

    private sealed class ComponentCache
    {
        public readonly Dictionary<int, int> Components;
        public readonly HashSet<EntityUid> Doors;

        public ComponentCache(Dictionary<int, int> components, HashSet<EntityUid> doors)
        {
            Components = components;
            Doors = doors;
        }
    }
}
