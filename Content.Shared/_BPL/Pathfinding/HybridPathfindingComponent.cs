namespace Content.Shared._BPL.Pathfinding;

/// <summary>
/// Opt this mob into the hybrid door-graph pathfinder.
/// Mobs without this component keep using Starlight's existing pathfinding.
/// </summary>
[RegisterComponent]
public sealed partial class HybridPathfindingComponent : Component;
