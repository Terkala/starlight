using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using Content.Server._BPL.Pathfinding;
using Robust.Shared.Analyzers;

namespace Content.Benchmarks._BPL.Pathfinding;

/// <summary>
/// Micro-benchmark for coarse reverse Dijkstra fan-in. Relative CPU vs agent count, not a full-server tick.
/// </summary>
[Virtual]
[MemoryDiagnoser]
public class PathBrokerCoarseFanInBenchmark
{
    [Params(1, 2, 8, 32)]
    public int Agents;

    private Dictionary<int, List<CoarseGraphSearch.Edge>> _adjacency = default!;

    [GlobalSetup]
    public void Setup()
    {
        const int rooms = 64;
        _adjacency = new Dictionary<int, List<CoarseGraphSearch.Edge>>();
        for (var i = 0; i < rooms; i++)
            _adjacency[i] = new List<CoarseGraphSearch.Edge>();

        var edgeId = 0;
        for (var i = 0; i < rooms - 1; i++)
        {
            var edge = new CoarseGraphSearch.Edge(edgeId++, i, i + 1, 1f);
            _adjacency[i].Add(edge);
            _adjacency[i + 1].Add(edge);
        }
    }

    [Benchmark]
    public int ReverseTrees()
    {
        var totalHops = 0;
        var goal = 63;
        var tree = CoarseGraphSearch.ReverseDijkstra(goal, _adjacency, _ => 1f);
        for (var i = 0; i < Agents; i++)
        {
            var start = i % 32;
            totalHops += CoarseGraphSearch.NextHopEdges(start, tree).Count;
        }

        return totalHops;
    }
}
