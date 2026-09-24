using System.Numerics;
using Content.Shared.Conveyor;
using Robust.Shared.Timing;

namespace Content.Server._BPL.Pathfinding;

/// <summary>
/// Keeps conveyor direction snapshots in sync for pathfinding workers.
/// Belt state changes do not rebuild the navmesh; A* reads this dictionary.
/// Cannot subscribe to Conveyor ComponentStartup/Shutdown/PowerChanged — those slots
/// are already taken by the conveyor controllers (one directed handler per pair).
/// </summary>
public sealed partial class ConveyorPathRevisionSystem : EntitySystem
{
    [Dependency] private DoorTraversalSystem _traversal = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private SharedTransformSystem _transform = default!;

    private TimeSpan _nextScan;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<ConveyorComponent, MoveEvent>(OnMove);
    }

    public override void Update(float frameTime)
    {
        if (_timing.CurTime < _nextScan)
            return;

        _nextScan = _timing.CurTime + TimeSpan.FromSeconds(0.5);

        var live = new HashSet<EntityUid>();
        var query = EntityQueryEnumerator<ConveyorComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var conv, out var xform))
        {
            live.Add(uid);
            if (_traversal.TryGetConveyorSnapshot(uid, out var snap) &&
                snap.State == conv.State &&
                snap.Powered == conv.Powered)
            {
                var dir = ConveyorPathSnapshot.ComputeWorldDirection(conv, _transform.GetWorldRotation(xform));
                if ((snap.WorldDirection - dir).LengthSquared() < 0.0001f)
                    continue;
            }

            Refresh(uid, conv, xform);
        }

        _traversal.PruneConveyorSnapshots(live);
    }

    private void OnMove(Entity<ConveyorComponent> ent, ref MoveEvent args)
    {
        Refresh(ent.Owner, ent.Comp, args.Component);
    }

    public void Refresh(EntityUid uid, ConveyorComponent? conv = null, TransformComponent? xform = null)
    {
        if (!Resolve(uid, ref conv, ref xform))
            return;

        var worldRot = _transform.GetWorldRotation(xform);
        _traversal.UpdateConveyorSnapshot(uid, ConveyorPathSnapshot.From(conv, worldRot));
    }
}
