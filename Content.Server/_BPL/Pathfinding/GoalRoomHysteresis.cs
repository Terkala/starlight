namespace Content.Server._BPL.Pathfinding;

/// <summary>
/// Two-tick goal-room hysteresis with immediate commit on force (door cross, stuck, invalid hop).
/// </summary>
public sealed class GoalRoomHysteresis
{
    public const byte CommitTicks = 2;

    public int? Committed { get; private set; }
    public int? Pending { get; private set; }
    public byte StableTicks { get; private set; }

    public int? Update(int observed, bool force)
    {
        if (Committed is null || force)
        {
            Committed = observed;
            Pending = observed;
            StableTicks = CommitTicks;
            return Committed;
        }

        if (observed == Committed)
        {
            Pending = observed;
            StableTicks = CommitTicks;
            return Committed;
        }

        if (Pending != observed)
        {
            Pending = observed;
            StableTicks = 0;
        }

        StableTicks++;
        if (StableTicks >= CommitTicks)
        {
            Committed = observed;
            Pending = observed;
            StableTicks = CommitTicks;
        }

        return Committed;
    }

    public void Reset()
    {
        Committed = null;
        Pending = null;
        StableTicks = 0;
    }
}
