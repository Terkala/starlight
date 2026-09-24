using Robust.Shared.Map;

namespace Content.Server._BPL.Pathfinding;

/// <summary>
/// Structural room/door multigraph for one grid. Topology only; door passability lives in semantic revisions.
/// </summary>
[RegisterComponent, Access(typeof(PathRoomGraphSystem), typeof(PathBrokerSystem))]
public sealed partial class PathRoomGraphComponent : Component
{
    [ViewVariables]
    public uint StructuralRevision;

    [ViewVariables]
    public int NextRoomId;

    [ViewVariables]
    public int NextEdgeId;

    [ViewVariables]
    public readonly Dictionary<int, PathRoom> Rooms = new();

    [ViewVariables]
    public readonly Dictionary<int, PathRoomEdge> Edges = new();

    [ViewVariables]
    public readonly Dictionary<Vector2i, int> TileToRoom = new();

    [ViewVariables]
    public readonly Dictionary<EntityUid, List<int>> DoorToEdges = new();

    [ViewVariables]
    public readonly HashSet<int> StaleRooms = new();

    [ViewVariables]
    public readonly HashSet<Vector2i> PendingDirtyTiles = new();

    [ViewVariables]
    public TimeSpan LastOccupiedHeartbeat;
}
