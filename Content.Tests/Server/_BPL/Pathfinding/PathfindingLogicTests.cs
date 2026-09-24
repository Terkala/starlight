using System.Collections.Generic;
using Content.Server._BPL.Pathfinding;
using Content.Server.NPC.Pathfinding;
using Content.Shared.Access;
using NUnit.Framework;
using Robust.Shared.Prototypes;

namespace Content.Tests.Server._BPL.Pathfinding;

[TestFixture]
[TestOf(typeof(PathAccessProfile))]
public sealed class PathAccessProfileTests
{
    [Test]
    public void CanonicalHashIgnoresTagOrder()
    {
        var a = PathAccessProfile.Create(
            new ProtoId<AccessLevelPrototype>[] { "Command", "Maintenance" },
            System.Array.Empty<ulong>(),
            PathFlags.Interact,
            1,
            2);
        var b = PathAccessProfile.Create(
            new ProtoId<AccessLevelPrototype>[] { "Maintenance", "Command" },
            System.Array.Empty<ulong>(),
            PathFlags.Interact,
            1,
            2);

        Assert.That(a, Is.EqualTo(b));
        Assert.That(a.GetHashCode(), Is.EqualTo(b.GetHashCode()));
    }

    [Test]
    public void DifferentFlagsAreNotEqual()
    {
        var a = PathAccessProfile.Create(
            new ProtoId<AccessLevelPrototype>[] { "Command" },
            System.Array.Empty<ulong>(),
            PathFlags.Interact,
            1,
            2);
        var b = PathAccessProfile.Create(
            new ProtoId<AccessLevelPrototype>[] { "Command" },
            System.Array.Empty<ulong>(),
            PathFlags.Prying,
            1,
            2);

        Assert.That(a, Is.Not.EqualTo(b));
    }

    [Test]
    public void WeightlessFlagDiffersFromInteract()
    {
        var grounded = PathAccessProfile.Create(
            new ProtoId<AccessLevelPrototype>[] { "Command" },
            System.Array.Empty<ulong>(),
            PathFlags.Interact,
            1,
            2);
        var floating = PathAccessProfile.Create(
            new ProtoId<AccessLevelPrototype>[] { "Command" },
            System.Array.Empty<ulong>(),
            PathFlags.Interact,
            1,
            2,
            canWeightless: true);

        Assert.That(grounded, Is.Not.EqualTo(floating));
        Assert.That(floating.CanWeightless, Is.True);
    }
}

[TestFixture]
[TestOf(typeof(CoarseGraphSearch))]
public sealed class CoarseGraphSearchTests
{
    [Test]
    public void AStarFindsDoorSequence()
    {
        var adjacency = new Dictionary<int, List<CoarseGraphSearch.Edge>>
        {
            [0] = new() { new(1, 0, 1, 1f) },
            [1] = new() { new(1, 0, 1, 1f), new(2, 1, 2, 1f) },
            [2] = new() { new(2, 1, 2, 1f) },
        };

        var path = CoarseGraphSearch.AStar(0, 2, adjacency, _ => 1f, (_, _) => 0f);
        Assert.That(path, Is.Not.Null);
        Assert.That(path!, Is.EqualTo(new[] { 1, 2 }));
    }

    [Test]
    public void BlockedEdgeDoesNotConnect()
    {
        var adjacency = new Dictionary<int, List<CoarseGraphSearch.Edge>>
        {
            [0] = new() { new(1, 0, 1, 1f) },
            [1] = new() { new(1, 0, 1, 1f) },
        };

        var path = CoarseGraphSearch.AStar(0, 1, adjacency, _ => null, (_, _) => 0f);
        Assert.That(path, Is.Null);
    }

    [Test]
    public void ReverseDijkstraNextHop()
    {
        var adjacency = new Dictionary<int, List<CoarseGraphSearch.Edge>>
        {
            [0] = new() { new(1, 0, 1, 1f) },
            [1] = new() { new(1, 0, 1, 1f), new(2, 1, 2, 1f) },
            [2] = new() { new(2, 1, 2, 1f) },
        };

        var tree = CoarseGraphSearch.ReverseDijkstra(2, adjacency, _ => 1f);
        Assert.That(tree[0].EdgeId, Is.EqualTo(1));
        Assert.That(tree[1].EdgeId, Is.EqualTo(2));
        var hops = CoarseGraphSearch.NextHopEdges(0, tree);
        Assert.That(hops, Is.EqualTo(new[] { 1, 2 }));
    }

    [Test]
    public void ConnectedComponentsSplitOnDeletedEdge()
    {
        var adjacency = new Dictionary<int, List<CoarseGraphSearch.Edge>>
        {
            [0] = new() { new(1, 0, 1, 1f) },
            [1] = new() { new(1, 0, 1, 1f), new(2, 1, 2, 1f) },
            [2] = new() { new(2, 1, 2, 1f) },
        };

        var open = CoarseGraphSearch.ConnectedComponents(new[] { 0, 1, 2 }, adjacency, _ => 1f);
        Assert.That(open[0], Is.EqualTo(open[2]));

        var closed = CoarseGraphSearch.ConnectedComponents(new[] { 0, 1, 2 }, adjacency, id => id == 2 ? null : 1f);
        Assert.That(closed[0], Is.Not.EqualTo(closed[2]));
    }
}

[TestFixture]
[TestOf(typeof(GoalRoomHysteresis))]
public sealed class GoalRoomHysteresisTests
{
    [Test]
    public void CommitsAfterTwoTicks()
    {
        var hyst = new GoalRoomHysteresis();
        Assert.That(hyst.Update(1, force: true), Is.EqualTo(1));
        Assert.That(hyst.Update(2, force: false), Is.EqualTo(1));
        Assert.That(hyst.Update(2, force: false), Is.EqualTo(2));
    }

    [Test]
    public void ForceCommitsImmediately()
    {
        var hyst = new GoalRoomHysteresis();
        hyst.Update(1, true);
        Assert.That(hyst.Update(3, true), Is.EqualTo(3));
    }
}

[TestFixture]
[TestOf(typeof(DeficitRoundRobinScheduler<int>))]
public sealed class SchedulerFairnessTests
{
    [Test]
    public void DoesNotStarveBackground()
    {
        var scheduler = new DeficitRoundRobinScheduler<int>();
        for (var i = 0; i < 10; i++)
            scheduler.Enqueue(PathBrokerPriority.Chase, i);

        scheduler.Enqueue(PathBrokerPriority.Background, 99);

        var seenBackground = false;
        var seenChase = 0;
        while (scheduler.TryDequeue(out var item))
        {
            if (item == 99)
                seenBackground = true;
            else
                seenChase++;
        }

        Assert.That(seenBackground, Is.True);
        Assert.That(seenChase, Is.EqualTo(10));
    }
}

[TestFixture]
[TestOf(typeof(DoorTraversalSystem))]
public sealed class DoorTraversalSnapshotTests
{
    [Test]
    public void AccessSubsetGrantsTraversal()
    {
        var profile = PathAccessProfile.Create(
            new ProtoId<AccessLevelPrototype>[] { "Command", "Maintenance" },
            System.Array.Empty<ulong>(),
            PathFlags.Interact,
            0,
            0);

        var snap = new DoorSemanticSnapshot(
            1,
            Content.Shared.Doors.Components.DoorState.Closed,
            true,
            false,
            false,
            true,
            false,
            false,
            false,
            true,
            true,
            true,
            false,
            System.Collections.Immutable.ImmutableArray.Create(
                System.Collections.Immutable.ImmutableArray.Create(new ProtoId<AccessLevelPrototype>("Command"))),
            System.Collections.Immutable.ImmutableArray<ProtoId<AccessLevelPrototype>>.Empty,
            System.Collections.Immutable.ImmutableArray<ulong>.Empty,
            0f);

        Assert.That(DoorTraversalSystem.AccessAllowed(profile, snap), Is.True);
    }

    [Test]
    public void MissingAccessDeniesTraversal()
    {
        var profile = PathAccessProfile.Create(
            new ProtoId<AccessLevelPrototype>[] { "Maintenance" },
            System.Array.Empty<ulong>(),
            PathFlags.None,
            0,
            0);

        var snap = new DoorSemanticSnapshot(
            1,
            Content.Shared.Doors.Components.DoorState.Closed,
            true,
            false,
            false,
            true,
            false,
            false,
            false,
            true,
            true,
            true,
            false,
            System.Collections.Immutable.ImmutableArray.Create(
                System.Collections.Immutable.ImmutableArray.Create(new ProtoId<AccessLevelPrototype>("Command"))),
            System.Collections.Immutable.ImmutableArray<ProtoId<AccessLevelPrototype>>.Empty,
            System.Collections.Immutable.ImmutableArray<ulong>.Empty,
            0f);

        Assert.That(DoorTraversalSystem.AccessAllowed(profile, snap), Is.False);
        var verdict = DoorTraversalSystem.EvaluateSnapshot(profile, snap, 0f, true);
        Assert.That(verdict.IsAllowed, Is.False);
    }

    [Test]
    public void BoltedDoorRequiresPryOrSmash()
    {
        var noForce = PathAccessProfile.Create(
            new ProtoId<AccessLevelPrototype>[] { "Command" },
            System.Array.Empty<ulong>(),
            PathFlags.Interact,
            0,
            0);
        var pry = PathAccessProfile.Create(
            new ProtoId<AccessLevelPrototype>[] { "Command" },
            System.Array.Empty<ulong>(),
            PathFlags.Interact | PathFlags.Prying,
            0,
            0);

        var snap = ClosedCommandDoor(bolted: true);
        Assert.That(DoorTraversalSystem.EvaluateSnapshot(noForce, snap, 0f, true).IsAllowed, Is.False);
        Assert.That(DoorTraversalSystem.EvaluateSnapshot(pry, snap, 0f, true).Kind,
            Is.EqualTo(DoorTraversalKind.PassExpensive));
    }

    [Test]
    public void AuthorizedClosedDoorIsInteract()
    {
        var profile = PathAccessProfile.Create(
            new ProtoId<AccessLevelPrototype>[] { "Command" },
            System.Array.Empty<ulong>(),
            PathFlags.Interact,
            0,
            0);
        var snap = ClosedCommandDoor(bolted: false);
        var verdict = DoorTraversalSystem.EvaluateSnapshot(profile, snap, 0f, true);
        Assert.That(verdict.IsAllowed, Is.True);
        Assert.That(verdict.Kind, Is.EqualTo(DoorTraversalKind.PassExpensive));
    }

    private static DoorSemanticSnapshot ClosedCommandDoor(bool bolted)
    {
        return new DoorSemanticSnapshot(
            1,
            Content.Shared.Doors.Components.DoorState.Closed,
            true,
            bolted,
            false,
            true,
            false,
            false,
            false,
            true,
            true,
            true,
            false,
            System.Collections.Immutable.ImmutableArray.Create(
                System.Collections.Immutable.ImmutableArray.Create(new ProtoId<AccessLevelPrototype>("Command"))),
            System.Collections.Immutable.ImmutableArray<ProtoId<AccessLevelPrototype>>.Empty,
            System.Collections.Immutable.ImmutableArray<ulong>.Empty,
            50f);
    }
}
