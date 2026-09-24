using Content.Shared.Access;
using Content.Shared.StationRecords;
using Robust.Shared.Prototypes;

namespace Content.Shared._BPL.Pathfinding;

/// <summary>
/// Copy of an access reader's requirements for pathfinding snapshots.
/// </summary>
public readonly record struct PathfindingAccessData(
    bool Enabled,
    bool ListsEmpty,
    ProtoId<AccessLevelPrototype>[][] AccessLists,
    ProtoId<AccessLevelPrototype>[] DenyTags,
    StationRecordKey[] AccessKeys);
