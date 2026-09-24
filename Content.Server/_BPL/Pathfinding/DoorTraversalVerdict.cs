namespace Content.Server._BPL.Pathfinding;

/// <summary>
/// Result of evaluating whether a mob may traverse a poly or door edge.
/// </summary>
public enum DoorTraversalKind : byte
{
    Blocked = 0,
    Pass = 1,
    PassExpensive = 2,
}

/// <summary>
/// Traversal outcome plus the cost multiplier applied to octile distance.
/// Blocked uses a multiplier of 0.
/// </summary>
public readonly record struct DoorTraversalVerdict(DoorTraversalKind Kind, float CostMultiplier)
{
    public static DoorTraversalVerdict Blocked => new(DoorTraversalKind.Blocked, 0f);

    public static DoorTraversalVerdict Pass => new(DoorTraversalKind.Pass, 1f);

    public static DoorTraversalVerdict Interact => new(DoorTraversalKind.PassExpensive, 1.5f);

    public static DoorTraversalVerdict Pry => new(DoorTraversalKind.PassExpensive, 10f);

    public static DoorTraversalVerdict Smash(float damage)
    {
        return new DoorTraversalVerdict(DoorTraversalKind.PassExpensive, 10f + damage / 100f);
    }

    public static DoorTraversalVerdict Climb => new(DoorTraversalKind.PassExpensive, 1.5f);

    /// <summary>
    /// Slightly worse than climbing: going prone is slower than vaulting.
    /// </summary>
    public static DoorTraversalVerdict Crawl => new(DoorTraversalKind.PassExpensive, 2f);

    /// <summary>
    /// Riding a belt in its heading is cheaper than walking the same tiles.
    /// </summary>
    public static DoorTraversalVerdict ConveyorWith => new(DoorTraversalKind.Pass, 0.65f);

    /// <summary>
    /// Walking against a running belt is possible but unoptimal.
    /// </summary>
    public static DoorTraversalVerdict ConveyorAgainst => new(DoorTraversalKind.PassExpensive, 2.5f);

    /// <summary>
    /// Crossing a belt perpendicular to its heading still gets pulled sideways.
    /// </summary>
    public static DoorTraversalVerdict ConveyorPerpendicular => new(DoorTraversalKind.PassExpensive, 1.4f);

    public bool IsAllowed => Kind != DoorTraversalKind.Blocked;
}
