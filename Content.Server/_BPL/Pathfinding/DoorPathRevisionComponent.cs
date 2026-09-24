using Content.Shared.Doors.Components;

namespace Content.Server._BPL.Pathfinding;

/// <summary>
/// Per-door semantic revision, updated on bolt/weld/power/access/open without changing room topology.
/// </summary>
[RegisterComponent, Access(typeof(DoorSemanticRevisionSystem), typeof(DoorTraversalSystem), typeof(PathRoomGraphSystem))]
public sealed partial class DoorPathRevisionComponent : Component
{
    [ViewVariables]
    public uint SemanticRevision;

    [ViewVariables]
    public DoorSemanticSnapshot Snapshot;
}
