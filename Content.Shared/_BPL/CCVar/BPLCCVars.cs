using Robust.Shared.Configuration;

namespace Content.Shared._BPL.CCVar;

/// <summary>
/// BPL-specific CVars. RT auto-discovers this class via the [CVarDefs] attribute.
/// </summary>
[CVarDefs]
public sealed class BPLCCVars
{
    /// <summary>
    /// When true, fine PathPoly A* uses the live door traversal predicate (access, bolt, weld, power, firelock)
    /// instead of baked Door/Access breadcrumb flags alone.
    /// </summary>
    public static readonly CVarDef<bool> PathfindingLivePredicate =
        CVarDef.Create("bpl.pathfinding.live_predicate", true, CVar.SERVER | CVar.ARCHIVE);

    /// <summary>
    /// When true, NPC steering uses the revisioned room/door broker (PRA* next-hop + optional shared reverse trees)
    /// instead of station-wide fine A*.
    /// </summary>
    public static readonly CVarDef<bool> PathfindingBroker =
        CVarDef.Create("bpl.pathfinding.broker", true, CVar.SERVER | CVar.ARCHIVE);

    /// <summary>
    /// When true, pure door open/close collision toggles do not dirty PathPoly chunks. Requires stable door-mouth
    /// polys. Falls back to normal dirtying if a door cannot be associated with its tile.
    /// </summary>
    public static readonly CVarDef<bool> PathfindingSuppressDoorRebuilds =
        CVarDef.Create("bpl.pathfinding.suppress_door_rebuilds", false, CVar.SERVER | CVar.ARCHIVE);
}
