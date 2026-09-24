namespace Content.Server._BPL.Pathfinding;

/// <summary>
/// Pure coarse graph algorithms used by the broker and unit tests.
/// Rooms are nodes; each door is a distinct undirected edge (multigraph).
/// </summary>
public static class CoarseGraphSearch
{
    public readonly record struct Edge(int Id, int RoomA, int RoomB, float Cost);

    public static List<int>? AStar(
        int startRoom,
        int goalRoom,
        IReadOnlyDictionary<int, List<Edge>> adjacency,
        Func<int, float?> edgeCost,
        Func<int, int, float> heuristic)
    {
        if (startRoom == goalRoom)
            return new List<int>();

        var frontier = new PriorityQueue<int, float>();
        var costSoFar = new Dictionary<int, float> { [startRoom] = 0f };
        var cameFromRoom = new Dictionary<int, int>();
        var cameFromEdge = new Dictionary<int, int>();

        frontier.Enqueue(startRoom, 0f);

        while (frontier.TryDequeue(out var current, out _))
        {
            if (current == goalRoom)
                return Reconstruct(cameFromRoom, cameFromEdge, startRoom, goalRoom);

            if (!adjacency.TryGetValue(current, out var edges))
                continue;

            foreach (var edge in edges)
            {
                var other = edge.RoomA == current ? edge.RoomB : edge.RoomA;
                if (other == current)
                    continue;

                var liveCost = edgeCost(edge.Id);
                if (liveCost is null)
                    continue;

                var newCost = costSoFar[current] + liveCost.Value;
                if (costSoFar.TryGetValue(other, out var prior) && newCost >= prior)
                    continue;

                costSoFar[other] = newCost;
                cameFromRoom[other] = current;
                cameFromEdge[other] = edge.Id;
                frontier.Enqueue(other, newCost + heuristic(other, goalRoom));
            }
        }

        return null;
    }

    /// <summary>
    /// Reverse Dijkstra from a goal room. Values are (parent room toward the goal, edge id, cost from goal).
    /// Walking parent pointers from any room yields the next hop toward the goal.
    /// </summary>
    public static Dictionary<int, (int ParentRoom, int EdgeId, float Cost)> ReverseDijkstra(
        int goalRoom,
        IReadOnlyDictionary<int, List<Edge>> adjacency,
        Func<int, float?> edgeCost)
    {
        var cost = new Dictionary<int, float> { [goalRoom] = 0f };
        var parent = new Dictionary<int, (int ParentRoom, int EdgeId, float Cost)>();
        var frontier = new PriorityQueue<int, float>();
        frontier.Enqueue(goalRoom, 0f);

        while (frontier.TryDequeue(out var current, out _))
        {
            if (!adjacency.TryGetValue(current, out var edges))
                continue;

            var currentCost = cost[current];
            foreach (var edge in edges)
            {
                var other = edge.RoomA == current ? edge.RoomB : edge.RoomA;
                if (other == current)
                    continue;

                var liveCost = edgeCost(edge.Id);
                if (liveCost is null)
                    continue;

                var newCost = currentCost + liveCost.Value;
                if (cost.TryGetValue(other, out var prior) && newCost >= prior)
                    continue;

                cost[other] = newCost;
                parent[other] = (current, edge.Id, newCost);
                frontier.Enqueue(other, newCost);
            }
        }

        return parent;
    }

    /// <summary>
    /// BFS connected components. Blocked edges (null cost) do not connect rooms.
    /// </summary>
    public static Dictionary<int, int> ConnectedComponents(
        IReadOnlyCollection<int> rooms,
        IReadOnlyDictionary<int, List<Edge>> adjacency,
        Func<int, float?> edgeCost)
    {
        var component = new Dictionary<int, int>();
        var next = 0;

        foreach (var room in rooms)
        {
            if (component.ContainsKey(room))
                continue;

            var id = next++;
            var queue = new Queue<int>();
            queue.Enqueue(room);
            component[room] = id;

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (!adjacency.TryGetValue(current, out var edges))
                    continue;

                foreach (var edge in edges)
                {
                    if (edgeCost(edge.Id) is null)
                        continue;

                    var other = edge.RoomA == current ? edge.RoomB : edge.RoomA;
                    if (!component.TryAdd(other, id))
                        continue;

                    queue.Enqueue(other);
                }
            }
        }

        return component;
    }

    public static List<int> NextHopEdges(
        int startRoom,
        Dictionary<int, (int ParentRoom, int EdgeId, float Cost)> tree)
    {
        var hops = new List<int>();
        var current = startRoom;
        var guard = 0;
        while (tree.TryGetValue(current, out var step) && guard++ < 4096)
        {
            hops.Add(step.EdgeId);
            current = step.ParentRoom;
        }

        return hops;
    }

    private static List<int> Reconstruct(
        Dictionary<int, int> cameFromRoom,
        Dictionary<int, int> cameFromEdge,
        int start,
        int goal)
    {
        var edges = new List<int>();
        var current = goal;
        while (current != start)
        {
            edges.Add(cameFromEdge[current]);
            current = cameFromRoom[current];
        }

        edges.Reverse();
        return edges;
    }
}
