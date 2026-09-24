using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Content.Shared.Climbing.Components;
using Content.Shared.Doors.Components;
using Content.Shared.NPC;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Events;
using Robust.Shared.Timing;

namespace Content.Server._BPL.Pathfinding;

/// <summary>
/// Builds and repairs a structural room/door multigraph. Door open/close never refloods rooms.
/// </summary>
public sealed partial class PathRoomGraphSystem : EntitySystem
{
    [Dependency] private DoorSemanticRevisionSystem _doorSemantics = default!;
    [Dependency] private DoorTraversalSystem _traversal = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private SharedMapSystem _maps = default!;
    [Dependency] private SharedTransformSystem _transform = default!;

    private EntityQuery<DoorComponent> _doorQuery;
    private EntityQuery<ClimbableComponent> _climbableQuery;
    private EntityQuery<FixturesComponent> _fixturesQuery;
    private EntityQuery<PhysicsComponent> _physicsQuery;
    private EntityQuery<PathRoomGraphComponent> _graphQuery;
    private EntityQuery<DoorPathRevisionComponent> _revQuery;
    private EntityQuery<TransformComponent> _xformQuery;

    private static readonly TimeSpan OccupiedHeartbeat = TimeSpan.FromSeconds(1.5);

    public override void Initialize()
    {
        base.Initialize();
        _doorQuery = GetEntityQuery<DoorComponent>();
        _climbableQuery = GetEntityQuery<ClimbableComponent>();
        _fixturesQuery = GetEntityQuery<FixturesComponent>();
        _physicsQuery = GetEntityQuery<PhysicsComponent>();
        _graphQuery = GetEntityQuery<PathRoomGraphComponent>();
        _revQuery = GetEntityQuery<DoorPathRevisionComponent>();
        _xformQuery = GetEntityQuery<TransformComponent>();

        SubscribeLocalEvent<GridInitializeEvent>(OnGridInit);
        SubscribeLocalEvent<GridRemovalEvent>(OnGridRemoved);
        SubscribeLocalEvent<TileChangedEvent>(OnTileChanged);
        SubscribeLocalEvent<CollisionChangeEvent>(OnCollisionChange);
        SubscribeLocalEvent<CollisionLayerChangeEvent>(OnCollisionLayerChange);
        SubscribeLocalEvent<DoorComponent, AnchorStateChangedEvent>(OnDoorAnchor);
        SubscribeLocalEvent<DoorComponent, ComponentStartup>(OnDoorStartup);
        SubscribeLocalEvent<DoorComponent, ComponentShutdown>(OnDoorShutdown);
        SubscribeLocalEvent<DoorComponent, DoorSemanticRevisionChangedEvent>(OnDoorSemantic);
        SubscribeLocalEvent<PathPortalChangedEvent>(OnPortalChanged);
    }

    private void OnGridInit(GridInitializeEvent ev)
    {
        EnsureComp<PathRoomGraphComponent>(ev.EntityUid);
        RebuildGrid(ev.EntityUid);
    }

    private void OnGridRemoved(GridRemovalEvent ev)
    {
        RemComp<PathRoomGraphComponent>(ev.EntityUid);
    }

    private void OnTileChanged(ref TileChangedEvent ev)
    {
        if (!_graphQuery.TryGetComponent(ev.Entity, out var graph))
            return;

        foreach (var change in ev.Changes)
        {
            if (change.OldTile.IsEmpty == change.NewTile.IsEmpty)
                continue;

            graph.PendingDirtyTiles.Add(change.GridIndices);
            MarkIntersectingStale(ev.Entity, graph, change.GridIndices);
        }
    }

    private void OnCollisionChange(ref CollisionChangeEvent ev)
    {
        // Door open/close is semantic, not topology.
        if (_doorQuery.HasComponent(ev.BodyUid))
            return;

        MarkEntityTileStale(ev.BodyUid);
    }

    private void OnCollisionLayerChange(ref CollisionLayerChangeEvent ev)
    {
        if (_doorQuery.HasComponent(ev.Body))
            return;

        MarkEntityTileStale(ev.Body);
    }

    private void OnDoorAnchor(Entity<DoorComponent> ent, ref AnchorStateChangedEvent args)
    {
        if (_xformQuery.TryGetComponent(ent.Owner, out var xform) && xform.GridUid is { } grid)
            RebuildGrid(grid);
    }

    private void OnDoorStartup(Entity<DoorComponent> ent, ref ComponentStartup args)
    {
        _doorSemantics.Refresh(ent.Owner);
        if (_xformQuery.TryGetComponent(ent.Owner, out var xform) && xform.GridUid is { } grid)
            RebuildGrid(grid);
    }

    private void OnDoorShutdown(Entity<DoorComponent> ent, ref ComponentShutdown args)
    {
        _traversal.RemoveSnapshot(ent.Owner);
        if (_xformQuery.TryGetComponent(ent.Owner, out var xform) && xform.GridUid is { } grid)
            RebuildGrid(grid);
    }

    private void OnDoorSemantic(Entity<DoorComponent> ent, ref DoorSemanticRevisionChangedEvent args)
    {
        if (!_xformQuery.TryGetComponent(ent.Owner, out var xform) || xform.GridUid is not { } grid)
            return;

        if (!_graphQuery.TryGetComponent(grid, out var graph))
            return;

        if (!graph.DoorToEdges.TryGetValue(ent.Owner, out var edgeIds))
            return;

        var revision = args.Revision;
        foreach (var edgeId in edgeIds)
        {
            if (graph.Edges.TryGetValue(edgeId, out var edge))
                edge.SemanticRevision = revision;
        }

        PathfindingMetrics.CacheInvalidations.WithLabels("edge").Inc();
        DoorEdgeInvalidated?.Invoke(grid, ent.Owner);
    }

    private void OnPortalChanged(ref PathPortalChangedEvent args)
    {
        var gridA = _transform.GetGrid(args.CoordinatesA);
        var gridB = _transform.GetGrid(args.CoordinatesB);
        if (gridA != null)
            RebuildGrid(gridA.Value);
        if (gridB != null && gridB != gridA)
            RebuildGrid(gridB.Value);
    }

    public event Action<EntityUid, EntityUid>? DoorEdgeInvalidated;
    public event Action<EntityUid>? StructuralInvalidated;

    public uint GetStructuralRevision(EntityUid grid)
    {
        return _graphQuery.TryGetComponent(grid, out var graph) ? graph.StructuralRevision : 0;
    }

    public PathRoomId? GetRoomAt(EntityCoordinates coords)
    {
        var grid = _transform.GetGrid(coords);
        if (grid == null || !_graphQuery.TryGetComponent(grid.Value, out var graph))
            return null;

        if (!TryComp(grid.Value, out MapGridComponent? mapGrid))
            return null;

        var tile = _maps.CoordinatesToTile(grid.Value, mapGrid, coords);
        if (graph.TileToRoom.TryGetValue(tile, out var localId))
            return new PathRoomId(grid.Value, localId);

        return null;
    }

    public PathRoomId? GetRoomAt(EntityUid grid, Vector2i tile)
    {
        if (!_graphQuery.TryGetComponent(grid, out var graph))
            return null;

        return graph.TileToRoom.TryGetValue(tile, out var localId)
            ? new PathRoomId(grid, localId)
            : null;
    }

    public PathRoomId? GetRoomAtOrAdjacent(EntityCoordinates coords)
    {
        var direct = GetRoomAt(coords);
        if (direct != null)
            return direct;

        var grid = _transform.GetGrid(coords);
        if (grid == null || !TryComp(grid.Value, out MapGridComponent? mapGrid))
            return null;

        var tile = _maps.CoordinatesToTile(grid.Value, mapGrid, coords);
        ReadOnlySpan<Vector2i> dirs = [Vector2i.Up, Vector2i.Down, Vector2i.Left, Vector2i.Right];
        foreach (var dir in dirs)
        {
            var nearby = GetRoomAt(grid.Value, tile + dir);
            if (nearby != null)
                return nearby;
        }

        return null;
    }

    public PathRoomId? GetDoorFarRoom(EntityUid grid, EntityUid door, PathRoomId? from)
    {
        if (!_graphQuery.TryGetComponent(grid, out var graph) ||
            !graph.DoorToEdges.TryGetValue(door, out var edgeIds))
        {
            return null;
        }

        foreach (var id in edgeIds)
        {
            if (!graph.Edges.TryGetValue(id, out var edge) || edge.Door != door)
                continue;

            var other = from is { } known && known.LocalId == edge.RoomA
                ? edge.RoomB
                : edge.RoomA;
            return new PathRoomId(grid, other);
        }

        return null;
    }

    public bool TryGetGraph(EntityUid grid, [NotNullWhen(true)] out PathRoomGraphComponent? graph)
    {
        return _graphQuery.TryGetComponent(grid, out graph);
    }

    public IReadOnlyList<PathRoomEdge> GetRoomEdges(PathRoomId room)
    {
        if (!_graphQuery.TryGetComponent(room.GridUid, out var graph) ||
            !graph.Rooms.TryGetValue(room.LocalId, out var data))
        {
            return Array.Empty<PathRoomEdge>();
        }

        var list = new List<PathRoomEdge>(data.EdgeIds.Count);
        foreach (var id in data.EdgeIds)
        {
            if (graph.Edges.TryGetValue(id, out var edge))
                list.Add(edge);
        }

        return list;
    }

    public Vector2? GetDoorMouth(PathRoomId room, EntityUid door)
    {
        if (!_graphQuery.TryGetComponent(room.GridUid, out var graph))
            return null;

        foreach (var edge in GetRoomEdges(room))
        {
            if (edge.Door != door)
                continue;

            return edge.RoomA == room.LocalId ? edge.MouthA : edge.MouthB;
        }

        return null;
    }

    public uint GetDoorRevision(EntityUid door)
    {
        return _revQuery.TryGetComponent(door, out var rev) ? rev.SemanticRevision : 0;
    }

    public int GetRoomCount(EntityUid grid)
    {
        return _graphQuery.TryGetComponent(grid, out var graph) ? graph.Rooms.Count : 0;
    }

    public bool HasDoorEdge(EntityUid grid, EntityUid door)
    {
        return _graphQuery.TryGetComponent(grid, out var graph) && graph.DoorToEdges.ContainsKey(door);
    }

    public bool DoorRequiresClimb(EntityUid grid, EntityUid door)
    {
        if (!_graphQuery.TryGetComponent(grid, out var graph) ||
            !graph.DoorToEdges.TryGetValue(door, out var edgeIds))
        {
            return false;
        }

        foreach (var id in edgeIds)
        {
            if (graph.Edges.TryGetValue(id, out var edge) && edge.RequiresClimb)
                return true;
        }

        return false;
    }

    public void MarkRoomStale(PathRoomId room)
    {
        if (!_graphQuery.TryGetComponent(room.GridUid, out var graph))
            return;

        if (!graph.Rooms.TryGetValue(room.LocalId, out var data))
            return;

        data.Stale = true;
        graph.StaleRooms.Add(room.LocalId);
    }

    public void RescanIfNeeded(EntityUid grid, PathRoomId? occupied, PathRoomId? lookahead)
    {
        if (!_graphQuery.TryGetComponent(grid, out var graph))
            return;

        if (graph.PendingDirtyTiles.Count > 0 || graph.Rooms.Count == 0)
        {
            RebuildGrid(grid);
            return;
        }

        TryRescanRoom(grid, graph, occupied);
        TryRescanRoom(grid, graph, lookahead);

        if (occupied != null && _timing.CurTime >= graph.LastOccupiedHeartbeat)
        {
            graph.LastOccupiedHeartbeat = _timing.CurTime + OccupiedHeartbeat;
            TryRescanRoom(grid, graph, occupied);
        }
    }

    private void TryRescanRoom(EntityUid grid, PathRoomGraphComponent graph, PathRoomId? room)
    {
        if (room is not { } id || id.GridUid != grid)
            return;

        if (!graph.Rooms.TryGetValue(id.LocalId, out var data) || !data.Stale)
            return;

        RebuildGrid(grid);
    }

    private void MarkEntityTileStale(EntityUid uid)
    {
        if (!_xformQuery.TryGetComponent(uid, out var xform) || xform.GridUid is not { } grid)
            return;

        if (!_graphQuery.TryGetComponent(grid, out var graph) || !TryComp(grid, out MapGridComponent? mapGrid))
            return;

        var tile = _maps.CoordinatesToTile(grid, mapGrid, xform.Coordinates);
        graph.PendingDirtyTiles.Add(tile);
        MarkIntersectingStale(grid, graph, tile);
    }

    private void MarkIntersectingStale(EntityUid grid, PathRoomGraphComponent graph, Vector2i tile)
    {
        if (graph.TileToRoom.TryGetValue(tile, out var roomId) &&
            graph.Rooms.TryGetValue(roomId, out var room))
        {
            room.Stale = true;
            graph.StaleRooms.Add(roomId);
        }
        else
        {
            foreach (var offset in _neighborOffsets)
            {
                var n = tile + offset;
                if (graph.TileToRoom.TryGetValue(n, out var nid) &&
                    graph.Rooms.TryGetValue(nid, out var nroom))
                {
                    nroom.Stale = true;
                    graph.StaleRooms.Add(nid);
                }
            }
        }
    }
}
