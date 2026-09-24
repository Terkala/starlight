using System.Threading;
using System.Threading.Tasks;
using Content.Server.NPC.Components;
using Content.Server.NPC.Systems;
using Content.Server.NPC.Pathfinding;
using Content.Shared._BPL.CCVar;
using Content.Shared.Climbing.Components;
using Content.Shared.Doors.Components;
using Content.Shared.Doors.Systems;
using Content.Shared.Interaction;
using Content.Shared.NPC;
using Content.Shared.Physics;
using Content.Shared.Stunnable;
using Robust.Shared.Configuration;
using Robust.Shared.Map;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Timing;

namespace Content.Server._BPL.Pathfinding;

public sealed partial class PathBrokerSystem : EntitySystem
{
    /// <summary>
    /// Arrival window for a door-mouth hop. Must be smaller than a tile or A* treats a nearby
    /// doorway as already reached and returns the NPC's current poly. 0.5 covers any sub-tile
    /// poly on the mouth tile without matching the opposite side of the door (~1 tile away).
    /// </summary>
    private const float DoorHopArriveRange = 0.5f;

    /// <summary>
    /// If farther than this from the near mouth, hop only to that tile. Closer, hop through
    /// the door to the far mouth.
    /// </summary>
    private const float ApproachDoorRange = 2f;

    [Dependency] private DoorTraversalSystem _traversal = default!;
    [Dependency] private SharedDoorSystem _doors = default!;
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private PathfindingSystem _pathfinding = default!;
    [Dependency] private PathRoomGraphSystem _rooms = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private SharedMapSystem _maps = default!;
    [Dependency] private SharedPhysicsSystem _physics = default!;
    [Dependency] private SharedTransformSystem _transform = default!;

    private bool _brokerEnabled = true;

    private readonly Dictionary<CoarseBatchKey, ReverseTreeCache> _reverseCache = new();
    private readonly Dictionary<CoarseBatchKey, ComponentCache> _components = new();
    private readonly Dictionary<EntityUid, GoalRoomHysteresis> _hysteresis = new();
    private readonly DeficitRoundRobinScheduler<CoarseBatchKey> _coarseQueue = new();
    private readonly HashSet<CoarseBatchKey> _queuedCoarse = new();
    private readonly Queue<SteerJob> _steerJobs = new();
    private readonly List<Task> _runningJobs = new();
    private readonly List<PathRequest> _fineRequests = new();

    private EntityQuery<NPCHybridPathComponent> _hybridQuery;
    private EntityQuery<NPCMeleeCombatComponent> _meleeQuery;
    private EntityQuery<PhysicsComponent> _physicsQuery;
    private EntityQuery<ClimbableComponent> _climbableQuery;
    private EntityQuery<ClimbingComponent> _climbingQuery;
    private EntityQuery<KnockedDownComponent> _knockedDownQuery;

    public bool Enabled => _brokerEnabled;

    public override void Initialize()
    {
        base.Initialize();
        _hybridQuery = GetEntityQuery<NPCHybridPathComponent>();
        _meleeQuery = GetEntityQuery<NPCMeleeCombatComponent>();
        _physicsQuery = GetEntityQuery<PhysicsComponent>();
        _climbableQuery = GetEntityQuery<ClimbableComponent>();
        _climbingQuery = GetEntityQuery<ClimbingComponent>();
        _knockedDownQuery = GetEntityQuery<KnockedDownComponent>();
        Subs.CVar(_cfg, BPLCCVars.PathfindingBroker, v => _brokerEnabled = v, true);
        // After steering enqueues, so this tick's hybrid work is timed as PathBrokerSystem.
        UpdatesAfter.Add(typeof(NPCSteeringSystem));
        _rooms.DoorEdgeInvalidated += OnDoorEdgeInvalidated;
        _rooms.StructuralInvalidated += OnStructuralInvalidated;
    }

    public override void Shutdown()
    {
        base.Shutdown();
        _rooms.DoorEdgeInvalidated -= OnDoorEdgeInvalidated;
        _rooms.StructuralInvalidated -= OnStructuralInvalidated;
    }

    public NPCHybridPathComponent EnsureHybrid(EntityUid uid)
    {
        return EnsureComp<NPCHybridPathComponent>(uid);
    }

    public void ClearHybrid(EntityUid uid)
    {
        RemComp<NPCHybridPathComponent>(uid);
        _hysteresis.Remove(uid);
    }

    /// <summary>
    /// Click-open the committed airlock once the mob is in interaction range.
    /// Stock steering only activates doors that are not access-locked.
    /// </summary>
    public void TryOpenCommittedDoor(EntityUid uid, NPCSteeringComponent steering)
    {
        if ((steering.Flags & PathFlags.Interact) == 0x0)
            return;

        if (!_hybridQuery.TryGetComponent(uid, out var hybrid) || hybrid.CommittedDoor is not { } door)
            return;

        if (!TryComp(door, out DoorComponent? doorComp) || doorComp.State != DoorState.Closed)
            return;

        if (!TryComp(uid, out TransformComponent? xform) || !TryComp(door, out TransformComponent? doorXform))
            return;

        if (!xform.Coordinates.TryDistance(EntityManager, doorXform.Coordinates, out var distance) ||
            distance > SharedInteractionSystem.InteractionRange)
            return;

        _doors.TryOpen(door, doorComp, uid, predicted: false, quiet: true);
    }

    public PathBrokerPriority GetPriority(EntityUid uid)
    {
        return _meleeQuery.HasComponent(uid) ? PathBrokerPriority.Chase : PathBrokerPriority.Normal;
    }

    public override void Update(float frameTime)
    {
        var started = 0;
        while (_steerJobs.Count > 0 && started < 32)
        {
            _runningJobs.Add(RunSteerJob(_steerJobs.Dequeue()));
            started++;
        }

        // Fine A* for hybrid mobs runs here, so robust_entity_systems_update_usage
        // labels it PathBrokerSystem instead of PathfindingSystem.
        for (var spin = 0; spin < 8 && _fineRequests.Count > 0; spin++)
            _pathfinding.ProcessQueuedRequests(_fineRequests);

        _runningJobs.RemoveAll(static task => task.IsCompleted);
    }

    public Task<HybridPathResult> RequestSteerPath(
        EntityUid agent,
        EntityCoordinates start,
        EntityCoordinates goal,
        float range,
        PathFlags flags,
        CancellationToken cancel)
    {
        var tcs = new TaskCompletionSource<HybridPathResult>();
        _steerJobs.Enqueue(new SteerJob(agent, start, goal, range, flags, cancel, tcs));
        return tcs.Task;
    }

    private async Task RunSteerJob(SteerJob job)
    {
        var result = new HybridPathResult();
        try
        {
            if (job.Cancel.IsCancellationRequested || TerminatingOrDeleted(job.Agent))
            {
                result.Result = PathResult.NoPath;
                PathfindingMetrics.BrokerResults.WithLabels("nopath").Inc();
            }
            else
            {
                result = await RequestSteerPathCore(job.Agent, job.Start, job.Goal, job.Range, job.Flags, job.Cancel);
            }
        }
        catch (Exception e)
        {
            Log.Error($"Hybrid path request failed for {ToPrettyString(job.Agent)}: {e}");
            result.Result = PathResult.NoPath;
            PathfindingMetrics.BrokerResults.WithLabels("nopath").Inc();
        }

        job.Tcs.TrySetResult(result);
    }

    private readonly record struct SteerJob(
        EntityUid Agent,
        EntityCoordinates Start,
        EntityCoordinates Goal,
        float Range,
        PathFlags Flags,
        CancellationToken Cancel,
        TaskCompletionSource<HybridPathResult> Tcs);

    private async Task<HybridPathResult> RequestSteerPathCore(
        EntityUid agent,
        EntityCoordinates start,
        EntityCoordinates goal,
        float range,
        PathFlags flags,
        CancellationToken cancel)
    {
        var result = new HybridPathResult();

        var layer = 0;
        var mask = 0;
        if (TryComp<FixturesComponent>(agent, out var fixtures))
            (layer, mask) = _physics.GetHardCollision(agent, fixtures);

        var profile = _traversal.BuildProfile(agent, flags, layer, mask);
        var hybrid = EnsureHybrid(agent);
        hybrid.Priority = GetPriority(agent);
        ExpirePenalties(hybrid);

        var observedStart = _rooms.GetRoomAt(start);
        var startRoom = ResolveStartRoom(start, hybrid);
        var observedGoal = _rooms.GetRoomAt(goal) ?? _rooms.GetRoomAtOrAdjacent(goal);
        hybrid.CurrentRoom = startRoom;
        result.CurrentRoom = startRoom;

        if (hybrid.CommittedDoor != null &&
            hybrid.CommittedFarRoom is { } arrivedFar &&
            observedStart is { } arrived &&
            arrived.LocalId == arrivedFar)
        {
            ClearCommittedHop(hybrid);
        }

        var forceGoal = hybrid.CommittedDoor == null ||
                        IsDoorPenalized(hybrid, hybrid.CommittedDoor.Value);
        var hysteresis = GetHysteresis(agent);
        PathRoomId? goalRoom = observedGoal;
        if (observedGoal is { } observed)
        {
            var committedLocal = hysteresis.Update(observed.LocalId, forceGoal);
            if (committedLocal != null)
                goalRoom = new PathRoomId(observed.GridUid, committedLocal.Value);
        }

        hybrid.GoalRoom = goalRoom;
        hybrid.Profile = profile;
        result.GoalRoom = goalRoom;

        // Same room: LOS or local fine A*.
        if (startRoom is { } sRoom && goalRoom is { } gRoom && sRoom == gRoom)
        {
            if (TrySameRoomLos(agent, goal))
            {
                result.Result = PathResult.Path;
                ClearCommittedHop(hybrid);
                PathfindingMetrics.BrokerResults.WithLabels("complete").Inc();
                return result;
            }

            var fine = await FinePath(agent, start, goal, range, flags, profile, sRoom.LocalId, null, cancel);
            return FinishAfterAwait(result, hybrid, agent, cancel, fine);
        }

        if (startRoom is not { } current || goalRoom is not { } targetRoom ||
            current.GridUid != targetRoom.GridUid)
        {
            var fallback = await FinePath(agent, start, goal, range, flags, profile, null, null, cancel);
            return FinishAfterAwait(result, hybrid, agent, cancel, fallback, clearCommitted: false);
        }

        if (TryReuseCommittedDoor(agent, hybrid, current, profile, out var committedDoor, out var mouth, out var reuseFar))
        {
            PathfindingMetrics.CommittedDoorReuse.Inc();
            var hop = await RunHop(agent, start, current, committedDoor, mouth, flags, profile, reuseFar, cancel);
            if (TerminatingOrDeleted(agent) || cancel.IsCancellationRequested)
            {
                result.Result = PathResult.NoPath;
                PathfindingMetrics.BrokerResults.WithLabels("nopath").Inc();
                return result;
            }

            if (IsUsableHop(hop.Result))
                return FinishHop(result, hybrid, hop, committedDoor, targetRoom);

            PenalizeDoor(hybrid, committedDoor);
            ClearCommittedHop(hybrid);
        }

        var next = FindNextDoor(agent, current, targetRoom, profile, hybrid);
        if (next is null)
            return await GoalFallback(result, hybrid, agent, start, goal, range, flags, profile, cancel);

        var (door, doorMouth, revision, farRoom) = next.Value;
        hybrid.CommittedDoor = door;
        hybrid.CommittedDoorRevision = revision;
        hybrid.CommittedFarRoom = farRoom;

        var hopPath = await RunHop(agent, start, current, door, doorMouth, flags, profile, farRoom, cancel);
        if (TerminatingOrDeleted(agent) || cancel.IsCancellationRequested)
        {
            result.Result = PathResult.NoPath;
            PathfindingMetrics.BrokerResults.WithLabels("nopath").Inc();
            return result;
        }

        if (!IsUsableHop(hopPath.Result))
        {
            PenalizeDoor(hybrid, door);
            ClearCommittedHop(hybrid);
            next = FindNextDoor(agent, current, targetRoom, profile, hybrid);
            if (next is null)
                return await GoalFallback(result, hybrid, agent, start, goal, range, flags, profile, cancel);

            (door, doorMouth, revision, farRoom) = next.Value;
            hybrid.CommittedDoor = door;
            hybrid.CommittedDoorRevision = revision;
            hybrid.CommittedFarRoom = farRoom;
            hopPath = await RunHop(agent, start, current, door, doorMouth, flags, profile, farRoom, cancel);
            if (TerminatingOrDeleted(agent) || cancel.IsCancellationRequested)
            {
                result.Result = PathResult.NoPath;
                PathfindingMetrics.BrokerResults.WithLabels("nopath").Inc();
                return result;
            }
        }

        if (!IsUsableHop(hopPath.Result))
            return await GoalFallback(result, hybrid, agent, start, goal, range, flags, profile, cancel);

        return FinishHop(result, hybrid, hopPath, door, targetRoom);
    }

    public bool IsReachable(EntityUid agent, EntityCoordinates start, EntityCoordinates goal, PathFlags flags)
    {
        var startRoom = _rooms.GetRoomAt(start);
        var goalRoom = _rooms.GetRoomAt(goal);
        if (startRoom is null || goalRoom is null)
            return true;

        if (startRoom == goalRoom)
            return true;

        if (startRoom.Value.GridUid != goalRoom.Value.GridUid)
            return true;

        var layer = 0;
        var mask = 0;
        if (TryComp<FixturesComponent>(agent, out var fixtures))
            (layer, mask) = _physics.GetHardCollision(agent, fixtures);

        var profile = _traversal.BuildProfile(agent, flags, layer, mask);
        var components = GetComponents(startRoom.Value.GridUid, profile);
        if (!components.TryGetValue(startRoom.Value.LocalId, out var a) ||
            !components.TryGetValue(goalRoom.Value.LocalId, out var b))
        {
            return false;
        }

        return a == b;
    }

    public bool ShouldRepath(EntityUid uid, NPCSteeringComponent steering, NPCHybridPathComponent hybrid)
    {
        if (hybrid.CommittedDoor is { } door)
        {
            if (IsDoorPenalized(hybrid, door))
                return true;

            if (_rooms.GetDoorRevision(door) != hybrid.CommittedDoorRevision)
                return true;
        }

        var currentGoal = _rooms.GetRoomAt(steering.Coordinates);
        if (currentGoal is { } observed &&
            hybrid.GoalRoom is { } committed &&
            observed.GridUid == committed.GridUid &&
            observed.LocalId != committed.LocalId)
        {
            var hyst = GetHysteresis(uid);
            var next = hyst.Update(observed.LocalId, false);
            return next != committed.LocalId;
        }

        return false;
    }

    public void NotifyStuck(EntityUid uid)
    {
        if (!_hybridQuery.TryGetComponent(uid, out var hybrid) || hybrid.CommittedDoor is not { } door)
            return;

        PenalizeDoor(hybrid, door);
        ClearCommittedHop(hybrid);
        if (_hysteresis.TryGetValue(uid, out var hyst))
            hyst.Reset();
    }

    public void NotifyObstacleFailed(EntityUid uid, PathPoly poly)
    {
        PathfindingMetrics.DoorObstacleFailures.Inc();
        _ = poly.IsValid();
        NotifyStuck(uid);
    }

    public void CancelRequest(EntityUid uid)
    {
        _hysteresis.Remove(uid);
    }

    private GoalRoomHysteresis GetHysteresis(EntityUid uid)
    {
        if (!_hysteresis.TryGetValue(uid, out var hysteresis))
        {
            hysteresis = new GoalRoomHysteresis();
            _hysteresis[uid] = hysteresis;
        }

        return hysteresis;
    }

    public void ProcessBroker(TimeSpan budget, Stopwatch stopwatch)
    {
        // Soft, stealable quotas: repair first, then coarse warmup; leftover budget stays for fine A*.
        var repairQuota = TimeSpan.FromTicks(Math.Min(budget.Ticks / 3, TimeSpan.FromMilliseconds(1).Ticks));
        var repairDeadline = stopwatch.Elapsed + repairQuota;
        if (repairDeadline > budget)
            repairDeadline = budget;

        var repairStart = stopwatch.Elapsed;
        var query = EntityQueryEnumerator<NPCSteeringComponent, NPCHybridPathComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out _, out var hybrid, out var xform))
        {
            if (stopwatch.Elapsed >= repairDeadline)
                break;

            try
            {
                if (xform.GridUid is not { } grid)
                    continue;

                _rooms.RescanIfNeeded(grid, hybrid.CurrentRoom, hybrid.GoalRoom);
            }
            catch (Exception e)
            {
                Log.Error($"Path broker repair failed for {ToPrettyString(uid)}: {e}");
            }
        }

        PathfindingMetrics.BrokerTime.WithLabels("repair").Observe((stopwatch.Elapsed - repairStart).TotalSeconds);
        PathfindingMetrics.BrokerPending.WithLabels("repair").Set(0);

        var coarseStart = stopwatch.Elapsed;
        while (stopwatch.Elapsed < budget && _coarseQueue.TryDequeue(out var key))
        {
            _queuedCoarse.Remove(key);
            try
            {
                if (!_rooms.TryGetGraph(key.Grid, out var graph) || graph.StructuralRevision != key.GraphRevision)
                    continue;

                GetOrBuildReverseTree(key, graph, key.Profile);
            }
            catch (Exception e)
            {
                Log.Error($"Path broker coarse warmup failed: {e}");
            }
        }

        PathfindingMetrics.BrokerTime.WithLabels("coarse").Observe((stopwatch.Elapsed - coarseStart).TotalSeconds);
        PathfindingMetrics.BrokerPending.WithLabels("coarse").Set(_coarseQueue.Count);
        PathfindingMetrics.BrokerPending.WithLabels("fine").Set(0);
    }

    private async Task<HybridPathResult> GoalFallback(
        HybridPathResult result,
        NPCHybridPathComponent hybrid,
        EntityUid agent,
        EntityCoordinates start,
        EntityCoordinates goal,
        float range,
        PathFlags flags,
        PathAccessProfile profile,
        CancellationToken cancel)
    {
        ClearCommittedHop(hybrid);
        var fallback = await FinePath(agent, start, goal, range, flags, profile, null, null, cancel);
        return FinishAfterAwait(result, hybrid, agent, cancel, fallback, clearCommitted: false);
    }

    private HybridPathResult FinishAfterAwait(
        HybridPathResult result,
        NPCHybridPathComponent hybrid,
        EntityUid agent,
        CancellationToken cancel,
        PathResultEvent hop,
        bool clearCommitted = true)
    {
        if (TerminatingOrDeleted(agent) || cancel.IsCancellationRequested)
        {
            result.Result = PathResult.NoPath;
            result.Path = new List<PathPoly>();
            PathfindingMetrics.BrokerResults.WithLabels("nopath").Inc();
            return result;
        }

        result.Result = hop.Result == PathResult.Path ? PathResult.Path : hop.Result;
        result.Path = hop.Path;
        if (clearCommitted)
            ClearCommittedHop(hybrid);
        PathfindingMetrics.BrokerResults.WithLabels(Label(result.Result)).Inc();
        return result;
    }

    private static void ClearCommittedHop(NPCHybridPathComponent hybrid)
    {
        hybrid.CommittedDoor = null;
        hybrid.CommittedFarRoom = null;
    }

    private static bool IsUsableHop(PathResult result)
    {
        return result is PathResult.Path or PathResult.PartialPath;
    }

    private void GetHopTarget(
        EntityCoordinates start,
        PathRoomId current,
        EntityUid door,
        EntityCoordinates farMouth,
        int farRoom,
        out EntityCoordinates target,
        out int? allowedRoom2)
    {
        target = farMouth;
        allowedRoom2 = farRoom;

        var nearPos = _rooms.GetDoorMouth(current, door);
        if (nearPos is null)
            return;

        if (_rooms.GetRoomAt(start) is null)
            return;

        var near = new EntityCoordinates(current.GridUid, nearPos.Value);
        if (!start.TryDistance(EntityManager, near, out var dist) || dist < ApproachDoorRange)
            return;

        target = near;
        allowedRoom2 = null;
    }

    private async Task<PathResultEvent> RunHop(
        EntityUid agent,
        EntityCoordinates start,
        PathRoomId current,
        EntityUid door,
        EntityCoordinates farMouth,
        PathFlags flags,
        PathAccessProfile profile,
        int farRoom,
        CancellationToken cancel)
    {
        GetHopTarget(start, current, door, farMouth, farRoom, out var target, out var allowed2);
        // FineTarget is on NPCHybridPathComponent; caller sets CommittedDoor before this.
        if (_hybridQuery.TryGetComponent(agent, out var hybrid))
            hybrid.FineTarget = target;

        var hop = await FinePath(
            agent,
            start,
            target,
            DoorHopArriveRange,
            flags,
            profile,
            current.LocalId,
            door,
            cancel,
            allowed2);

        if (!IsUsableHop(hop.Result) && allowed2 is null)
        {
            hop = await FinePath(
                agent,
                start,
                farMouth,
                DoorHopArriveRange,
                flags,
                profile,
                current.LocalId,
                door,
                cancel,
                farRoom);
            target = farMouth;
        }

        if (!IsUsableHop(hop.Result))
        {
            hop = await FinePath(
                agent,
                start,
                target,
                DoorHopArriveRange,
                flags,
                profile,
                null,
                door,
                cancel);
        }

        if (allowed2 != null &&
            (!IsUsableHop(hop.Result) || !PathCrossesDoor(hop.Path, door)))
        {
            if (TryBridgeDoor(start, door, farMouth, out var bridged))
                hop = bridged;
        }

        return hop;
    }

    private static bool PathCrossesDoor(List<PathPoly> path, EntityUid door)
    {
        foreach (var poly in path)
        {
            if ((poly.Data.Flags & PathfindingBreadcrumbFlag.Door) != 0x0)
                return true;
        }

        return false;
    }

    private bool TryBridgeDoor(
        EntityCoordinates start,
        EntityUid door,
        EntityCoordinates farMouth,
        out PathResultEvent result)
    {
        result = new PathResultEvent(PathResult.NoPath, new List<PathPoly>());
        if (!TryComp(door, out TransformComponent? doorXform))
            return false;

        var startPoly = _pathfinding.GetPoly(start);
        var doorPoly = _pathfinding.GetPoly(doorXform.Coordinates);
        var endPoly = _pathfinding.GetPoly(farMouth);
        if (startPoly == null || doorPoly == null || endPoly == null)
            return false;

        var path = new List<PathPoly>(3);
        path.Add(startPoly);
        if (!startPoly.Equals(doorPoly))
            path.Add(doorPoly);
        if (!doorPoly.Equals(endPoly))
            path.Add(endPoly);

        result = new PathResultEvent(PathResult.PartialPath, path);
        return path.Count >= 2;
    }

    private static string Label(PathResult result)
    {
        return result switch
        {
            PathResult.Path => "complete",
            PathResult.PartialPath => "partial",
            PathResult.Continuing => "continuing",
            _ => "nopath",
        };
    }

    /// <summary>
    /// Door tiles are not flooded into rooms. Keep the origin room until the NPC actually
    /// enters <see cref="NPCHybridPathComponent.CommittedFarRoom"/> so hops do not flip sides.
    /// </summary>
    private PathRoomId? ResolveStartRoom(EntityCoordinates start, NPCHybridPathComponent hybrid)
    {
        var startRoom = _rooms.GetRoomAt(start);
        if (startRoom != null)
            return startRoom;

        if (hybrid.CommittedFarRoom is { } farId &&
            hybrid.CurrentRoom is { } origin &&
            origin.LocalId != farId)
        {
            return origin;
        }

        var grid = _transform.GetGrid(start);
        if (grid != null && hybrid.CommittedDoor is { } door)
        {
            var far = _rooms.GetDoorFarRoom(grid.Value, door, hybrid.CurrentRoom);
            if (far != null)
                return far;
        }

        return _rooms.GetRoomAtOrAdjacent(start);
    }
}
