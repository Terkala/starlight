using System.Numerics;
using Robust.Shared.Map;

namespace Content.Server._BPL.Pathfinding;

public sealed class PathRoom
{
    public int LocalId;
    public Vector2i SeedTile;
    public int TileCount;
    public readonly HashSet<EntityUid> Doors = new();
    public readonly HashSet<int> EdgeIds = new();
    public bool Stale;
    public TimeSpan LastScan;
    public ulong Signature;
}

public sealed class PathRoomEdge
{
    public int EdgeId;
    public EntityUid? Door;
    public int RoomA;
    public int RoomB;
    public Vector2 MouthA;
    public Vector2 MouthB;
    public uint SemanticRevision;
    public bool IsPortal;
    public int? PortalHandle;
    /// <summary>
    /// True when this door shares a tile with a climbable (windoor on a table).
    /// Coarse routing must require climb or crawl in addition to door access.
    /// </summary>
    public bool RequiresClimb;
}
