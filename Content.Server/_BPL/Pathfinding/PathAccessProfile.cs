using System.Collections.Immutable;
using System.Linq;
using Content.Server.NPC.Pathfinding;
using Content.Shared.Access;
using Robust.Shared.Prototypes;

namespace Content.Server._BPL.Pathfinding;

/// <summary>
/// Immutable per-request access and movement capability snapshot.
/// Built once per path request; never rebuilt per A* neighbor.
/// Stores the sorted tag set itself so a hash collision cannot grant access.
/// </summary>
public sealed class PathAccessProfile : IEquatable<PathAccessProfile>
{
    public static readonly PathAccessProfile Empty = new(
        ImmutableArray<ProtoId<AccessLevelPrototype>>.Empty,
        ImmutableArray<ulong>.Empty,
        PathFlags.None,
        0,
        0,
        false,
        false);

    public readonly ImmutableArray<ProtoId<AccessLevelPrototype>> Tags;
    public readonly ImmutableArray<ulong> StationRecordHashes;
    public readonly PathFlags Flags;
    public readonly int CollisionLayer;
    public readonly int CollisionMask;

    /// <summary>
    /// Can go prone under tables. Stored here so the shared <see cref="PathFlags"/> enum stays unchanged.
    /// </summary>
    public readonly bool CanCrawl;

    /// <summary>
    /// Can still move after gravity loss. Stored here so the shared pathfinder does not grow a new flag.
    /// </summary>
    public readonly bool CanWeightless;

    public PathAccessProfile(
        ImmutableArray<ProtoId<AccessLevelPrototype>> tags,
        ImmutableArray<ulong> stationRecordHashes,
        PathFlags flags,
        int collisionLayer,
        int collisionMask,
        bool canCrawl = false,
        bool canWeightless = false)
    {
        Tags = tags;
        StationRecordHashes = stationRecordHashes;
        Flags = flags;
        CollisionLayer = collisionLayer;
        CollisionMask = collisionMask;
        CanCrawl = canCrawl;
        CanWeightless = canWeightless;
    }

    public static PathAccessProfile Create(
        IEnumerable<ProtoId<AccessLevelPrototype>> tags,
        IEnumerable<ulong> stationRecordHashes,
        PathFlags flags,
        int collisionLayer,
        int collisionMask,
        bool canCrawl = false,
        bool canWeightless = false)
    {
        var sortedTags = tags
            .Distinct()
            .OrderBy(t => t.Id, StringComparer.Ordinal)
            .ToImmutableArray();

        var sortedRecords = stationRecordHashes
            .Distinct()
            .OrderBy(h => h)
            .ToImmutableArray();

        return new PathAccessProfile(sortedTags, sortedRecords, flags, collisionLayer, collisionMask, canCrawl, canWeightless);
    }

    public bool HasFlag(PathFlags flag)
    {
        return (Flags & flag) != 0;
    }

    public bool Equals(PathAccessProfile? other)
    {
        if (other is null)
            return false;

        if (ReferenceEquals(this, other))
            return true;

        if (Flags != other.Flags ||
            CanCrawl != other.CanCrawl ||
            CanWeightless != other.CanWeightless ||
            CollisionLayer != other.CollisionLayer ||
            CollisionMask != other.CollisionMask ||
            Tags.Length != other.Tags.Length ||
            StationRecordHashes.Length != other.StationRecordHashes.Length)
        {
            return false;
        }

        for (var i = 0; i < Tags.Length; i++)
        {
            if (Tags[i] != other.Tags[i])
                return false;
        }

        for (var i = 0; i < StationRecordHashes.Length; i++)
        {
            if (StationRecordHashes[i] != other.StationRecordHashes[i])
                return false;
        }

        return true;
    }

    public override bool Equals(object? obj)
    {
        return Equals(obj as PathAccessProfile);
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add((int) Flags);
        hash.Add(CanCrawl);
        hash.Add(CanWeightless);
        hash.Add(CollisionLayer);
        hash.Add(CollisionMask);

        foreach (var tag in Tags)
        {
            hash.Add(tag.Id);
        }

        foreach (var record in StationRecordHashes)
        {
            hash.Add(record);
        }

        return hash.ToHashCode();
    }
}
