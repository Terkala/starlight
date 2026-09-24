using Prometheus;

namespace Content.Server._BPL.Pathfinding;

/// <summary>
/// Prometheus counters for pathfinding and the hybrid broker.
/// </summary>
public static class PathfindingMetrics
{
    public static readonly Counter FineRequests = Metrics.CreateCounter(
        "npc_pathfinding_fine_requests_total",
        "Fine PathPoly path requests enqueued.");

    public static readonly Counter NodeLimitFailures = Metrics.CreateCounter(
        "npc_pathfinding_node_limit_failures_total",
        "Fine A* searches that hit NodeLimit without arriving.");

    public static readonly Histogram QueueLatency = Metrics.CreateHistogram(
        "npc_pathfinding_queue_latency_seconds",
        "Time from enqueue to terminal PathResult.");

    public static readonly Counter ChunkRebuilds = Metrics.CreateCounter(
        "npc_pathfinding_chunk_rebuilds_total",
        "PathPoly chunk dirty events by cause.",
        "cause");

    public static readonly Counter InvalidPolyRepaths = Metrics.CreateCounter(
        "npc_pathfinding_invalid_poly_repaths_total",
        "Steering replans caused by Invalid PathPoly flags.");

    public static readonly Counter DoorObstacleFailures = Metrics.CreateCounter(
        "npc_pathfinding_door_obstacle_failures_total",
        "Steering obstacle handlers that failed on a door poly.");

    public static readonly Gauge BrokerPending = Metrics.CreateGauge(
        "path_broker_pending",
        "Pending broker work by phase.",
        "phase");

    public static readonly Histogram BrokerTime = Metrics.CreateHistogram(
        "path_broker_time_seconds",
        "Broker phase wall time per tick.",
        "phase");

    public static readonly Histogram CoarseBatchSize = Metrics.CreateHistogram(
        "path_broker_coarse_batch_size",
        "Number of NPCs sharing a coarse batch key.");

    public static readonly Counter CacheHits = Metrics.CreateCounter(
        "path_broker_cache_hits_total",
        "Broker cache hits.",
        "kind");

    public static readonly Counter CacheInvalidations = Metrics.CreateCounter(
        "path_broker_cache_invalidations_total",
        "Broker cache invalidations.",
        "reason");

    public static readonly Counter BrokerResults = Metrics.CreateCounter(
        "path_broker_results_total",
        "Terminal broker results.",
        "status");

    public static readonly Counter CommittedDoorReuse = Metrics.CreateCounter(
        "path_broker_committed_door_reuse_total",
        "Times a committed door hop skipped coarse search.");

    public static readonly Counter RoomRescans = Metrics.CreateCounter(
        "path_broker_room_rescans_total",
        "Occupied/stale room flood-fills.");
}
