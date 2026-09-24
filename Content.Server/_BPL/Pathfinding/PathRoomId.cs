namespace Content.Server._BPL.Pathfinding;

/// <summary>
/// Identifies a flooded structural room on a grid.
/// </summary>
public readonly record struct PathRoomId(EntityUid GridUid, int LocalId)
{
    public bool IsValid => GridUid.IsValid() && LocalId >= 0;

    public static PathRoomId Invalid => new(EntityUid.Invalid, -1);
}
