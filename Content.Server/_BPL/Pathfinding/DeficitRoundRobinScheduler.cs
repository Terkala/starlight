namespace Content.Server._BPL.Pathfinding;

/// <summary>
/// Deficit round-robin across priority bands so chase work cannot starve wander jobs.
/// </summary>
public sealed class DeficitRoundRobinScheduler<T>
{
    public static readonly int[] Weights =
    {
        1, // Background
        1, // Normal
        3, // Chase
        4, // Critical
    };

    private readonly Queue<T>[] _queues;
    private readonly int[] _deficit;

    public DeficitRoundRobinScheduler()
    {
        var bands = Weights.Length;
        _queues = new Queue<T>[bands];
        _deficit = new int[bands];
        for (var i = 0; i < bands; i++)
        {
            _queues[i] = new Queue<T>();
            _deficit[i] = Weights[i];
        }
    }

    public int Count
    {
        get
        {
            var total = 0;
            foreach (var queue in _queues)
            {
                total += queue.Count;
            }

            return total;
        }
    }

    public int CountAt(PathBrokerPriority priority)
    {
        return _queues[(int) priority].Count;
    }

    public void Enqueue(PathBrokerPriority priority, T item)
    {
        _queues[(int) priority].Enqueue(item);
    }

    public bool TryDequeue(out T item)
    {
        for (var pass = 0; pass < Weights.Length; pass++)
        {
            for (var i = Weights.Length - 1; i >= 0; i--)
            {
                if (_queues[i].Count == 0)
                    continue;

                _deficit[i]++;
                if (_deficit[i] < Weights[i])
                    continue;

                _deficit[i] -= Weights[i];
                item = _queues[i].Dequeue();
                return true;
            }
        }

        item = default!;
        return false;
    }

    public void Clear()
    {
        foreach (var queue in _queues)
        {
            queue.Clear();
        }
    }
}
