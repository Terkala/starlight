using System.Collections.Immutable;
using Content.Shared.Access;
using Content.Shared.Doors.Components;
using Robust.Shared.Prototypes;

namespace Content.Server._BPL.Pathfinding;

/// <summary>
/// Main-thread snapshot of a door's semantic state, safe to read from pathfinding workers.
/// </summary>
public readonly record struct DoorSemanticSnapshot(
    uint Revision,
    DoorState State,
    bool Collidable,
    bool Bolted,
    bool Welded,
    bool Powered,
    bool EmergencyAccess,
    bool Firelock,
    bool FirelockLocked,
    bool BumpOpen,
    bool HasAccessReader,
    bool AccessReaderEnabled,
    bool AccessListsEmpty,
    ImmutableArray<ImmutableArray<ProtoId<AccessLevelPrototype>>> AccessLists,
    ImmutableArray<ProtoId<AccessLevelPrototype>> DenyTags,
    ImmutableArray<ulong> AccessKeyHashes,
    float SmashDamage);
