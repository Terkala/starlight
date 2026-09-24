using Content.Server.NPC.Pathfinding;
using Robust.Shared.Map;

namespace Content.Server._BPL.Pathfinding;

/// <summary>
/// Per-NPC hybrid pathfinding state. Attached while steering is active.
/// </summary>
[RegisterComponent, Access(typeof(PathBrokerSystem), typeof(PathRoomGraphSystem))]
public sealed partial class NPCHybridPathComponent : Component
{
    [ViewVariables]
    public EntityUid? CommittedDoor;

    [ViewVariables]
    public uint CommittedDoorRevision;

    /// <summary>
    /// Room on the far side of <see cref="CommittedDoor"/>. Kept stable while standing on the
    /// door tile so hops do not flip back to the origin room.
    /// </summary>
    [ViewVariables]
    public int? CommittedFarRoom;

    [ViewVariables]
    public PathRoomId? GoalRoom;

    [ViewVariables]
    public PathRoomId? PendingGoalRoom;

    [ViewVariables]
    public byte GoalRoomStableTicks;

    [ViewVariables]
    public PathRoomId? CurrentRoom;

    [ViewVariables]
    public EntityCoordinates FineTarget;

    [ViewVariables]
    public PathBrokerPriority Priority = PathBrokerPriority.Normal;

    [ViewVariables]
    public PathAccessProfile? Profile;

    /// <summary>
    /// Short-lived door-mouth penalties after a blocked hop. Value is expiry time.
    /// </summary>
    [ViewVariables]
    public readonly Dictionary<EntityUid, TimeSpan> DoorPenalties = new();

    [ViewVariables]
    public int? CoarseRequestId;
}
