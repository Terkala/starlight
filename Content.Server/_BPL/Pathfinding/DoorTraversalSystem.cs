using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Numerics;
using Content.Server.NPC.Pathfinding;
using Content.Shared._BPL.CCVar;
using Content.Shared.Access;
using Content.Shared.Access.Components;
using Content.Shared.Access.Systems;
using Content.Shared.CCVar;
using Content.Shared.Doors.Components;
using Content.Shared.Gravity;
using Content.Shared.Stunnable;
using Content.Shared.Movement.Components;
using Content.Shared.Movement.Events;
using Content.Shared.NPC;
using Content.Shared.StationRecords;
using Robust.Shared.Configuration;
using Robust.Shared.Physics.Components;
using Robust.Shared.Prototypes;

namespace Content.Server._BPL.Pathfinding;

/// <summary>
/// Authoritative door/poly traversal predicate for coarse routing, fine GetTileCost, and steering.
/// Door snapshots are refreshed on the main thread; A* workers only read the concurrent dictionary.
/// </summary>
public sealed partial class DoorTraversalSystem : EntitySystem
{
    [Dependency] private AccessReaderSystem _access = default!;
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private SharedGravitySystem _gravity = default!;

    private readonly ConcurrentDictionary<EntityUid, DoorSemanticSnapshot> _snapshots = new();
    private readonly ConcurrentDictionary<EntityUid, ConveyorPathSnapshot> _conveyors = new();

    /// <summary>
    /// Travel vs belt heading: above this is "with", below the negation is "against".
    /// </summary>
    public const float ConveyorAlignDot = 0.35f;

    private EntityQuery<DoorPathRevisionComponent> _revisionQuery;
    private EntityQuery<MovementAlwaysTouchingComponent> _alwaysTouchingQuery;
    private EntityQuery<CanMoveInAirComponent> _canMoveInAirQuery;
    private EntityQuery<JetpackUserComponent> _jetpackQuery;
    private EntityQuery<MovementIgnoreGravityComponent> _ignoreGravityQuery;
    private bool _livePredicate = true;

    public override void Initialize()
    {
        base.Initialize();
        _revisionQuery = GetEntityQuery<DoorPathRevisionComponent>();
        _alwaysTouchingQuery = GetEntityQuery<MovementAlwaysTouchingComponent>();
        _canMoveInAirQuery = GetEntityQuery<CanMoveInAirComponent>();
        _jetpackQuery = GetEntityQuery<JetpackUserComponent>();
        _ignoreGravityQuery = GetEntityQuery<MovementIgnoreGravityComponent>();
        Subs.CVar(_cfg, BPLCCVars.PathfindingLivePredicate, v => _livePredicate = v, true);
    }

    public bool LivePredicateEnabled => _livePredicate;

    public PathAccessProfile BuildProfile(EntityUid mob, PathFlags flags, int collisionLayer, int collisionMask)
    {
        var tags = _access.FindAccessTags(mob);
        _access.FindStationRecordKeys(mob, out var keys);
        var hashes = new List<ulong>();
        foreach (var key in keys)
        {
            hashes.Add(HashRecordKey(key));
        }

        var canCrawl = HasComp<CrawlerComponent>(mob) && _cfg.GetCVar(CCVars.MovementCrawling);
        return PathAccessProfile.Create(
            tags,
            hashes,
            flags,
            collisionLayer,
            collisionMask,
            canCrawl,
            CanMoveWhileWeightless(mob));
    }

    /// <summary>
    /// True if this mob can still accelerate after gravity loss: dedicated thrusters/jetpack,
    /// or anyone currently weightless while on a grid (SharedMoverController treats that as touching).
    /// </summary>
    public bool CanMoveWhileWeightless(EntityUid mob)
    {
        if (_alwaysTouchingQuery.HasComponent(mob) ||
            _canMoveInAirQuery.HasComponent(mob) ||
            _jetpackQuery.HasComponent(mob) ||
            _ignoreGravityQuery.HasComponent(mob))
        {
            return true;
        }

        var ev = new CanWeightlessMoveEvent(mob);
        RaiseLocalEvent(mob, ref ev, true);
        if (ev.CanMove)
            return true;

        return _gravity.IsWeightless(mob) && Transform(mob).GridUid != null;
    }

    public void UpdateSnapshot(EntityUid door, DoorSemanticSnapshot snapshot)
    {
        _snapshots[door] = snapshot;
    }

    public void RemoveSnapshot(EntityUid door)
    {
        _snapshots.TryRemove(door, out _);
    }

    public bool TryGetSnapshot(EntityUid door, out DoorSemanticSnapshot snapshot)
    {
        return _snapshots.TryGetValue(door, out snapshot);
    }

    public void UpdateConveyorSnapshot(EntityUid belt, ConveyorPathSnapshot snapshot)
    {
        _conveyors[belt] = snapshot;
    }

    public void RemoveConveyorSnapshot(EntityUid belt)
    {
        _conveyors.TryRemove(belt, out _);
    }

    public bool TryGetConveyorSnapshot(EntityUid belt, out ConveyorPathSnapshot snapshot)
    {
        return _conveyors.TryGetValue(belt, out snapshot);
    }

    public void PruneConveyorSnapshots(HashSet<EntityUid> live)
    {
        foreach (var key in _conveyors.Keys)
        {
            if (!live.Contains(key))
                _conveyors.TryRemove(key, out _);
        }
    }

    public static ulong HashRecordKey(StationRecordKey key)
    {
        return ((ulong) key.Id << 32) ^ (ulong) (uint) key.OriginStation.Id;
    }

    /// <summary>
    /// Cost multiplier for a destination poly. 0 is impassable.
    /// </summary>
    public float GetCostModifier(PathRequest request, PathPoly start, PathPoly end)
    {
        var verdict = EvaluatePoly(PathAccessProfile.Empty, end, request.CollisionLayer, request.CollisionMask, start);
        return verdict.IsAllowed ? verdict.CostMultiplier : 0f;
    }

    /// <summary>
    /// Poly check used by the hybrid broker. Door identity stays on the room graph; the shared
    /// navmesh is not tagged with door entities.
    /// </summary>
    public DoorTraversalVerdict EvaluatePoly(
        PathAccessProfile profile,
        PathPoly end,
        int collisionLayer,
        int collisionMask,
        PathPoly? start = null)
    {
        if ((end.Data.Flags & PathfindingBreadcrumbFlag.Space) != 0x0)
            return DoorTraversalVerdict.Blocked;

        var collides = (collisionLayer & end.Data.CollisionMask) != 0x0 ||
                       (collisionMask & end.Data.CollisionLayer) != 0x0;

        var verdict = DoorTraversalVerdict.Pass;
        var isDoor = (end.Data.Flags & PathfindingBreadcrumbFlag.Door) != 0x0;
        if (isDoor)
            verdict = FallbackDoorFlags(profile, collides);

        if (collides)
        {
            var hasClimb = (end.Data.Flags & PathfindingBreadcrumbFlag.Climb) != 0x0;
            if (hasClimb)
                verdict = Combine(verdict, EvaluateClimbFlap(profile, true, false));
            else if (!isDoor)
                verdict = Combine(verdict, EvaluateNonDoorObstacle(profile, end));
        }

        verdict = Combine(verdict, EvaluateConveyor(profile, start, end));
        return verdict;
    }

    public DoorTraversalVerdict EvaluateDoorEdge(PathAccessProfile profile, EntityUid door, bool requiresClimb = false)
    {
        var doorVerdict = EvaluateDoor(profile, door, smashDamage: 0f, collides: true);
        if (!requiresClimb)
            return doorVerdict;

        return Combine(doorVerdict, EvaluateClimbRequirement(profile));
    }

    public static DoorTraversalVerdict EvaluateClimbRequirement(PathAccessProfile profile)
    {
        if (profile.HasFlag(PathFlags.Climbing))
            return DoorTraversalVerdict.Climb;

        if (profile.CanCrawl)
            return DoorTraversalVerdict.Crawl;

        return DoorTraversalVerdict.Blocked;
    }

    public bool IsPolyTraversableNow(PathRequest request, PathPoly poly)
    {
        return EvaluatePoly(PathAccessProfile.Empty, poly, request.CollisionLayer, request.CollisionMask).IsAllowed;
    }

    public bool ShouldExecuteObstacle(PathRequest request, PathPoly poly)
    {
        var verdict = EvaluatePoly(PathAccessProfile.Empty, poly, request.CollisionLayer, request.CollisionMask);
        return verdict.Kind == DoorTraversalKind.PassExpensive;
    }

    public DoorTraversalVerdict EvaluateDoor(
        PathAccessProfile profile,
        EntityUid door,
        float smashDamage,
        bool collides)
    {
        if (!TryGetSnapshot(door, out var snap))
        {
            if (_revisionQuery.TryGetComponent(door, out var rev))
                snap = rev.Snapshot;
            else
                return FallbackDoorFlags(profile, collides);
        }

        return EvaluateSnapshot(profile, snap, smashDamage, collides);
    }

    public static DoorTraversalVerdict EvaluateSnapshot(
        PathAccessProfile profile,
        in DoorSemanticSnapshot snap,
        float smashDamage,
        bool collides)
    {
        var damage = smashDamage > 0f ? smashDamage : snap.SmashDamage;

        if (snap.Welded)
            return TryForce(profile, damage);

        if (snap.Bolted)
            return TryForce(profile, damage);

        // Open / non-colliding door: walk through.
        if (!snap.Collidable ||
            snap.State is DoorState.Open or DoorState.Opening or DoorState.Emagging)
        {
            return DoorTraversalVerdict.Pass;
        }

        if (snap.EmergencyAccess)
            return DoorTraversalVerdict.Interact;

        if (snap.Firelock)
        {
            if (snap.FirelockLocked)
            {
                if (snap.Powered && AccessAllowed(profile, snap))
                    return DoorTraversalVerdict.Interact;

                return TryForce(profile, damage);
            }

            // Closed unlocked firelock: click-open / bump.
            if (!snap.Powered)
                return TryForce(profile, damage);

            return snap.BumpOpen ? DoorTraversalVerdict.Pass : DoorTraversalVerdict.Interact;
        }

        if (snap.HasAccessReader && snap.AccessReaderEnabled && !snap.AccessListsEmpty)
        {
            if (AccessAllowed(profile, snap))
            {
                if (!snap.Powered)
                    return TryForce(profile, damage);

                return DoorTraversalVerdict.Interact;
            }

            return TryForce(profile, damage);
        }

        // Unpowered airlocks cannot bump-open.
        if (!snap.Powered && snap.HasAccessReader)
            return TryForce(profile, damage);

        if (snap.BumpOpen)
            return DoorTraversalVerdict.Pass;

        if (profile.HasFlag(PathFlags.Interact))
            return DoorTraversalVerdict.Interact;

        return DoorTraversalVerdict.Blocked;
    }

    /// <summary>
    /// A windoor+table poly is allowed only if the agent can handle every obstacle on it.
    /// Climb (vault/crawl table) and flaps (crawl-under only) are separate.
    /// </summary>
    public static DoorTraversalVerdict EvaluateClimbFlap(
        PathAccessProfile profile,
        bool hasClimb,
        bool hasFlap)
    {
        var verdict = DoorTraversalVerdict.Pass;
        if (hasClimb)
            verdict = Combine(verdict, EvaluateClimbRequirement(profile));
        if (hasFlap)
            verdict = Combine(verdict, EvaluateFlapRequirement(profile));
        return verdict;
    }

    public static DoorTraversalVerdict EvaluateFlapRequirement(PathAccessProfile profile)
    {
        if (profile.CanCrawl)
            return DoorTraversalVerdict.Crawl;

        return DoorTraversalVerdict.Blocked;
    }

    /// <summary>
    /// True if crawling under this flap would fight a running belt. Direction-only: with-the-belt is allowed.
    /// Flap tiles are not baked into the shared navmesh, so this stays false unless a belt snapshot is resolved.
    /// </summary>
    public bool IsOpposingFlapConveyor(PathPoly poly, Vector2 travel)
    {
        if (!TryResolveConveyor(poly, out var snap) || !snap.Running)
            return false;

        if (travel.LengthSquared() < 0.0001f)
            return false;

        return Vector2.Dot(Vector2.Normalize(travel), snap.WorldDirection) < -ConveyorAlignDot;
    }

    public DoorTraversalVerdict EvaluateConveyor(PathAccessProfile profile, PathPoly? start, PathPoly end)
    {
        if (!TryResolveConveyor(end, out var snap) || !snap.Running)
            return DoorTraversalVerdict.Pass;

        if (start == null)
            return DoorTraversalVerdict.Pass;

        var travel = end.Box.Center - start.Box.Center;
        if (travel.LengthSquared() < 0.0001f)
            return DoorTraversalVerdict.Pass;

        var dot = Vector2.Dot(Vector2.Normalize(travel), snap.WorldDirection);
        var opposing = dot < -ConveyorAlignDot;
        var withBelt = dot > ConveyorAlignDot;

        if (withBelt)
            return DoorTraversalVerdict.ConveyorWith;
        if (opposing)
            return DoorTraversalVerdict.ConveyorAgainst;
        return DoorTraversalVerdict.ConveyorPerpendicular;
    }

    private bool TryResolveConveyor(PathPoly poly, out ConveyorPathSnapshot snap)
    {
        snap = default;
        return false;
    }

    public static DoorTraversalVerdict Combine(DoorTraversalVerdict a, DoorTraversalVerdict b)
    {
        if (!a.IsAllowed)
            return a;
        if (!b.IsAllowed)
            return b;

        var kind = a.Kind == DoorTraversalKind.PassExpensive || b.Kind == DoorTraversalKind.PassExpensive
            ? DoorTraversalKind.PassExpensive
            : DoorTraversalKind.Pass;
        return new DoorTraversalVerdict(kind, a.CostMultiplier * b.CostMultiplier);
    }

    private static DoorTraversalVerdict EvaluateNonDoorObstacle(PathAccessProfile profile, PathPoly end)
    {
        var isClimb = (end.Data.Flags & PathfindingBreadcrumbFlag.Climb) != 0x0;

        if (isClimb && profile.HasFlag(PathFlags.Climbing))
            return DoorTraversalVerdict.Climb;

        if (isClimb && profile.CanCrawl)
            return DoorTraversalVerdict.Crawl;

        if (profile.HasFlag(PathFlags.Smashing) && end.Data.Damage > 0f)
            return DoorTraversalVerdict.Smash(end.Data.Damage);

        return DoorTraversalVerdict.Blocked;
    }

    private static DoorTraversalVerdict FallbackDoorFlags(PathAccessProfile profile, bool collides)
    {
        if (!collides)
            return DoorTraversalVerdict.Pass;

        if (profile.HasFlag(PathFlags.Prying))
            return DoorTraversalVerdict.Pry;

        if (profile.HasFlag(PathFlags.Interact))
            return DoorTraversalVerdict.Interact;

        return DoorTraversalVerdict.Blocked;
    }

    private static DoorTraversalVerdict TryForce(PathAccessProfile profile, float damage)
    {
        if (profile.HasFlag(PathFlags.Prying))
            return DoorTraversalVerdict.Pry;

        if (profile.HasFlag(PathFlags.Smashing) && damage > 0f)
            return DoorTraversalVerdict.Smash(damage);

        return DoorTraversalVerdict.Blocked;
    }

    public static bool AccessAllowed(PathAccessProfile profile, in DoorSemanticSnapshot snap)
    {
        if (!snap.HasAccessReader || !snap.AccessReaderEnabled || snap.AccessListsEmpty)
            return true;

        foreach (var deny in snap.DenyTags)
        {
            if (profile.Tags.Contains(deny))
                return false;
        }

        foreach (var set in snap.AccessLists)
        {
            var match = true;
            foreach (var tag in set)
            {
                if (!profile.Tags.Contains(tag))
                {
                    match = false;
                    break;
                }
            }

            if (match)
                return true;
        }

        if (snap.AccessKeyHashes.Length == 0)
            return false;

        foreach (var keyHash in snap.AccessKeyHashes)
        {
            if (profile.StationRecordHashes.Contains(keyHash))
                return true;
        }

        return false;
    }
}
