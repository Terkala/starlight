using Robust.Shared.GameStates;

namespace Content.Shared._BPL.Pathfinding;

/// <summary>
/// Marker for plastic flaps: standing is blocked, crawlers can go under.
/// Combined with an opposing conveyor, crawl-through is impassable in that direction only.
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class PlasticFlapsComponent : Component;
