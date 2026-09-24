using System;
using System.Collections.Concurrent;
using System.Numerics;
using BenchmarkDotNet.Attributes;
using Content.Server._BPL.Pathfinding;
using Content.Server.NPC.Pathfinding;
using Content.Shared.Access;
using Content.Shared.Conveyor;
using Robust.Shared.Analyzers;
using Robust.Shared.GameObjects;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;

namespace Content.Benchmarks._BPL.Pathfinding;

/// <summary>
/// Per-edge live-predicate cost added since the 2026-08-25 11:05 estimate:
/// conveyor snapshot lookup, directional classify, climb/flap combine.
/// 512 expansions = one FinePath time slice.
/// </summary>
[Virtual]
[MemoryDiagnoser]
public class PathfindingLiveCostBenchmark
{
    private const int SliceExpansions = 512;
    private const int HardLimitExpansions = 4096;

    private ConcurrentDictionary<EntityUid, ConveyorPathSnapshot> _snaps = default!;
    private PathAccessProfile _crawler = default!;
    private EntityUid _beltUid;
    private Vector2 _start;
    private Vector2 _endFloor;
    private Vector2 _endBelt;
    private Vector2 _beltDir;
    private ConveyorPathSnapshot _snap;

    [GlobalSetup]
    public void Setup()
    {
        _beltUid = new EntityUid(42);
        _beltDir = Angle.FromDegrees(90).ToWorldVec();
        _snap = new ConveyorPathSnapshot(true, _beltDir, 2f, ConveyorState.Forward, true);
        _snaps = new ConcurrentDictionary<EntityUid, ConveyorPathSnapshot>();
        for (var i = 1; i <= 400; i++)
            _snaps[new EntityUid(i)] = _snap;
        _snaps[_beltUid] = _snap;
        _crawler = PathAccessProfile.Create(
            Array.Empty<ProtoId<AccessLevelPrototype>>(),
            Array.Empty<ulong>(),
            PathFlags.Interact,
            0,
            0,
            canCrawl: true);
        _start = new Vector2(0.5f, 1.5f);
        _endFloor = new Vector2(1.5f, 1.5f);
        _endBelt = new Vector2(2.5f, 1.5f);
    }

    [Benchmark(Baseline = true, Description = "512 edges, Aug 25 11:05 (door/climb flags only)")]
    public float BaselineSlice512()
    {
        var sum = 0f;
        for (var i = 0; i < SliceExpansions; i++)
            sum += BaselineEdge(climb: i % 17 == 0);
        return sum;
    }

    [Benchmark(Description = "512 edges, open floor (ConveyorEntity null)")]
    public float CurrentFloorSlice512()
    {
        var sum = 0f;
        for (var i = 0; i < SliceExpansions; i++)
            sum += CurrentEdge(hasBelt: false, hasFlap: false, climb: i % 17 == 0);
        return sum;
    }

    [Benchmark(Description = "512 edges, riding a belt")]
    public float CurrentBeltSlice512()
    {
        var sum = 0f;
        for (var i = 0; i < SliceExpansions; i++)
            sum += CurrentEdge(hasBelt: true, hasFlap: false, climb: false);
        return sum;
    }

    [Benchmark(Description = "512 edges, flap + opposing belt")]
    public float CurrentFlapOpposingSlice512()
    {
        var sum = 0f;
        for (var i = 0; i < SliceExpansions; i++)
            sum += CurrentEdge(hasBelt: true, hasFlap: true, climb: false, opposing: true);
        return sum;
    }

    [Benchmark(Description = "4096 edges, open floor (worst FinePath)")]
    public float CurrentFloorHardLimit()
    {
        var sum = 0f;
        for (var i = 0; i < HardLimitExpansions; i++)
            sum += CurrentEdge(hasBelt: false, hasFlap: false, climb: false);
        return sum;
    }

    [Benchmark(Description = "Scan 400 belts (0.5s conveyor snapshot pass)")]
    public int ConveyorScan400()
    {
        var dirty = 0;
        for (var i = 1; i <= 400; i++)
        {
            var uid = new EntityUid(i);
            if (_snaps.TryGetValue(uid, out var snap) &&
                snap.State == ConveyorState.Forward &&
                snap.Powered &&
                (snap.WorldDirection - _beltDir).LengthSquared() < 0.0001f)
            {
                continue;
            }

            dirty++;
        }

        return dirty;
    }

    private float BaselineEdge(bool climb)
    {
        var verdict = DoorTraversalVerdict.Pass;
        if (climb)
            verdict = DoorTraversalSystem.Combine(verdict, DoorTraversalSystem.EvaluateClimbRequirement(_crawler));
        return verdict.IsAllowed ? verdict.CostMultiplier : 0f;
    }

    private float CurrentEdge(bool hasBelt, bool hasFlap, bool climb, bool opposing = false)
    {
        var verdict = DoorTraversalVerdict.Pass;
        if (climb || hasFlap)
            verdict = DoorTraversalSystem.Combine(verdict, DoorTraversalSystem.EvaluateClimbFlap(_crawler, climb, hasFlap));

        verdict = DoorTraversalSystem.Combine(verdict, EvaluateConveyor(hasBelt, hasFlap, opposing));
        return verdict.IsAllowed ? verdict.CostMultiplier : 0f;
    }

    private DoorTraversalVerdict EvaluateConveyor(bool hasBelt, bool hasFlap, bool opposing)
    {
        if (!hasBelt)
            return DoorTraversalVerdict.Pass;

        if (!_snaps.TryGetValue(_beltUid, out var snap) || !snap.Running)
            return DoorTraversalVerdict.Pass;

        var travel = opposing ? _start - _endBelt : _endBelt - _start;
        if (travel.LengthSquared() < 0.0001f)
            return DoorTraversalVerdict.Pass;

        var dot = Vector2.Dot(Vector2.Normalize(travel), snap.WorldDirection);
        if (dot < -DoorTraversalSystem.ConveyorAlignDot && hasFlap)
            return DoorTraversalVerdict.Blocked;
        if (dot > DoorTraversalSystem.ConveyorAlignDot)
            return DoorTraversalVerdict.ConveyorWith;
        if (dot < -DoorTraversalSystem.ConveyorAlignDot)
            return DoorTraversalVerdict.ConveyorAgainst;
        return DoorTraversalVerdict.ConveyorPerpendicular;
    }
}
