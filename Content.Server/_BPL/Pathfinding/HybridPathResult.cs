using Content.Server.NPC.Pathfinding;
using Robust.Shared.Map;

namespace Content.Server._BPL.Pathfinding;

public sealed class HybridPathResult
{
    public PathResult Result;
    public List<PathPoly> Path = new();
    public EntityUid? CommittedDoor;
    public PathRoomId? GoalRoom;
    public PathRoomId? CurrentRoom;
}

public readonly record struct CoarseBatchKey(
    EntityUid Grid,
    int GoalRoom,
    PathAccessProfile Profile,
    uint GraphRevision);
