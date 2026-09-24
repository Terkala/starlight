using Robust.Shared.Map;

namespace Content.Server._BPL.Pathfinding;

/// <summary>
/// Raised on a door when its semantic revision increments.
/// </summary>
[ByRefEvent]
public readonly record struct DoorSemanticRevisionChangedEvent(EntityUid Door, uint Revision);

/// <summary>
/// Broadcast when a PathPortal is created or removed.
/// </summary>
[ByRefEvent]
public readonly record struct PathPortalChangedEvent(
    int Handle,
    EntityCoordinates CoordinatesA,
    EntityCoordinates CoordinatesB,
    bool Added);
