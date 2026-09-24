using Robust.Shared.GameStates;

namespace Content.Shared._BPL.Pathfinding;

/// <summary>
/// Admin pathfinding test dummy. Interact to pick a station beacon; it will walk there.
/// </summary>
[RegisterComponent]
public sealed partial class DeliveryBoyComponent : Component
{
    /// <summary>
    /// How close to the beacon counts as arrived.
    /// </summary>
    [DataField]
    public float ArriveRange = 1.5f;
}
