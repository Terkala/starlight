using System.Collections.Immutable;
using Content.Server.Access;
using Content.Server.Destructible;
using Content.Shared.Access;
using Content.Shared.Access.Components;
using Content.Shared.Access.Systems;
using Content.Shared.Doors;
using Content.Shared.Doors.Components;
using Content.Shared.Power;
using Content.Shared.Tools.Components;
using Content.Shared.Tools.Systems;
using Robust.Shared.Physics.Components;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._BPL.Pathfinding;

/// <summary>
/// Keeps per-door semantic revisions and thread-safe snapshots in sync with door/access/power/weld state.
/// </summary>
public sealed partial class DoorSemanticRevisionSystem : EntitySystem
{
    [Dependency] private AccessReaderSystem _access = default!;
    [Dependency] private DestructibleSystem _destructible = default!;
    [Dependency] private DoorTraversalSystem _traversal = default!;
    [Dependency] private IGameTiming _timing = default!;

    private EntityQuery<DoorComponent> _doorQuery;
    private EntityQuery<DoorBoltComponent> _boltQuery;
    private EntityQuery<DoorPathRevisionComponent> _revQuery;
    private EntityQuery<WeldableComponent> _weldQuery;
    private EntityQuery<AirlockComponent> _airlockQuery;
    private EntityQuery<FirelockComponent> _firelockQuery;
    private EntityQuery<AccessReaderComponent> _accessQuery;
    private EntityQuery<PhysicsComponent> _physicsQuery;
    private EntityQuery<DestructibleComponent> _destructibleQuery;

    private TimeSpan _nextFirelockScan;

    public override void Initialize()
    {
        base.Initialize();
        _doorQuery = GetEntityQuery<DoorComponent>();
        _boltQuery = GetEntityQuery<DoorBoltComponent>();
        _revQuery = GetEntityQuery<DoorPathRevisionComponent>();
        _weldQuery = GetEntityQuery<WeldableComponent>();
        _airlockQuery = GetEntityQuery<AirlockComponent>();
        _firelockQuery = GetEntityQuery<FirelockComponent>();
        _accessQuery = GetEntityQuery<AccessReaderComponent>();
        _physicsQuery = GetEntityQuery<PhysicsComponent>();
        _destructibleQuery = GetEntityQuery<DestructibleComponent>();

        // DoorComponent + DoorStateChangedEvent is already taken by ScentSystem.
        SubscribeLocalEvent<DoorPathRevisionComponent, DoorStateChangedEvent>(OnDoorState);
        SubscribeLocalEvent<DoorBoltComponent, DoorBoltsChangedEvent>(OnBolts);
        SubscribeLocalEvent<WeldableComponent, WeldableChangedEvent>(OnWeld);
        SubscribeLocalEvent<DoorComponent, PowerChangedEvent>(OnPower);
        SubscribeLocalEvent<AccessReaderComponent, AccessReaderConfigurationChangedEvent>(OnAccessConfig);
        SubscribeLocalEvent<AccessReaderChangeEvent>(OnAccessReaderChange);
    }

    public override void Update(float frameTime)
    {
        if (_timing.CurTime < _nextFirelockScan)
            return;

        _nextFirelockScan = _timing.CurTime + TimeSpan.FromSeconds(1);

        var query = EntityQueryEnumerator<FirelockComponent, DoorPathRevisionComponent>();
        while (query.MoveNext(out var uid, out var firelock, out var rev))
        {
            if (rev.Snapshot.FirelockLocked == firelock.IsLocked &&
                rev.Snapshot.Powered == firelock.Powered)
            {
                continue;
            }

            Refresh(uid);
        }
    }

    private void OnDoorState(Entity<DoorPathRevisionComponent> ent, ref DoorStateChangedEvent args)
    {
        Refresh(ent.Owner);
    }

    private void OnBolts(Entity<DoorBoltComponent> ent, ref DoorBoltsChangedEvent args)
    {
        Refresh(ent.Owner);
    }

    private void OnWeld(Entity<WeldableComponent> ent, ref WeldableChangedEvent args)
    {
        if (_doorQuery.HasComponent(ent.Owner))
            Refresh(ent.Owner);
    }

    private void OnPower(Entity<DoorComponent> ent, ref PowerChangedEvent args)
    {
        Refresh(ent.Owner);
    }

    private void OnAccessConfig(Entity<AccessReaderComponent> ent, ref AccessReaderConfigurationChangedEvent args)
    {
        if (_doorQuery.HasComponent(ent.Owner))
            Refresh(ent.Owner);
    }

    private void OnAccessReaderChange(AccessReaderChangeEvent args)
    {
        if (_doorQuery.HasComponent(args.Sender))
            Refresh(args.Sender);
    }

    public void Refresh(EntityUid door)
    {
        if (!_doorQuery.TryGetComponent(door, out var doorComp))
            return;

        var rev = EnsureComp<DoorPathRevisionComponent>(door);
        var snapshot = BuildSnapshot(door, doorComp, rev.SemanticRevision + 1);

        if (SnapshotsEqual(rev.Snapshot, snapshot) && rev.SemanticRevision != 0)
            return;

        rev.SemanticRevision = snapshot.Revision;
        rev.Snapshot = snapshot;
        _traversal.UpdateSnapshot(door, snapshot);

        var ev = new DoorSemanticRevisionChangedEvent(door, snapshot.Revision);
        RaiseLocalEvent(door, ref ev);
    }

    private DoorSemanticSnapshot BuildSnapshot(EntityUid door, DoorComponent doorComp, uint revision)
    {
        var bolted = _boltQuery.TryGetComponent(door, out var bolts) && bolts.BoltsDown;
        var welded = doorComp.State == DoorState.Welded ||
                     (_weldQuery.TryGetComponent(door, out var weld) && weld.IsWelded);
        var collidable = !_physicsQuery.TryGetComponent(door, out var physics) || physics.CanCollide;

        var powered = true;
        var emergency = false;
        if (_airlockQuery.TryGetComponent(door, out var airlock))
        {
            powered = airlock.Powered;
            emergency = airlock.EmergencyAccess;
        }

        var firelock = false;
        var firelockLocked = false;
        if (_firelockQuery.TryGetComponent(door, out var firelockComp))
        {
            firelock = true;
            firelockLocked = firelockComp.IsLocked;
            powered = firelockComp.Powered;
        }

        var hasReader = false;
        var readerEnabled = true;
        var listsEmpty = true;
        var accessLists = ImmutableArray<ImmutableArray<ProtoId<AccessLevelPrototype>>>.Empty;
        var deny = ImmutableArray<ProtoId<AccessLevelPrototype>>.Empty;
        var keyHashes = ImmutableArray<ulong>.Empty;

        if (_access.GetMainAccessReader(door, out var readerEnt))
        {
            hasReader = true;
            var reader = readerEnt.Value.Comp;
            readerEnabled = reader.Enabled;
            listsEmpty = reader.AccessLists.Count == 0;

            var builder = ImmutableArray.CreateBuilder<ImmutableArray<ProtoId<AccessLevelPrototype>>>(reader.AccessLists.Count);
            foreach (var set in reader.AccessLists)
            {
                builder.Add(set.ToImmutableArray());
            }

            accessLists = builder.ToImmutable();
            deny = reader.DenyTags.ToImmutableArray();

            var keys = ImmutableArray.CreateBuilder<ulong>(reader.AccessKeys.Count);
            foreach (var key in reader.AccessKeys)
            {
                keys.Add(DoorTraversalSystem.HashRecordKey(key));
            }

            keyHashes = keys.ToImmutable();
        }

        var damage = 0f;
        if (_destructibleQuery.TryGetComponent(door, out var destructible))
            damage = _destructible.DestroyedAt(door, destructible).Float();

        return new DoorSemanticSnapshot(
            revision,
            doorComp.State,
            collidable,
            bolted,
            welded,
            powered,
            emergency,
            firelock,
            firelockLocked,
            doorComp.BumpOpen,
            hasReader,
            readerEnabled,
            listsEmpty,
            accessLists,
            deny,
            keyHashes,
            damage);
    }

    private static bool SnapshotsEqual(in DoorSemanticSnapshot a, in DoorSemanticSnapshot b)
    {
        return a.State == b.State &&
               a.Collidable == b.Collidable &&
               a.Bolted == b.Bolted &&
               a.Welded == b.Welded &&
               a.Powered == b.Powered &&
               a.EmergencyAccess == b.EmergencyAccess &&
               a.Firelock == b.Firelock &&
               a.FirelockLocked == b.FirelockLocked &&
               a.BumpOpen == b.BumpOpen &&
               a.HasAccessReader == b.HasAccessReader &&
               a.AccessReaderEnabled == b.AccessReaderEnabled &&
               a.AccessListsEmpty == b.AccessListsEmpty &&
               a.SmashDamage.Equals(b.SmashDamage);
    }
}
