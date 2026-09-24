namespace Content.Server._BPL.Pathfinding;

public enum PathBrokerPriority : byte
{
    Background = 0,
    Normal = 1,
    Chase = 2,
    Critical = 3,
}
