# Hybrid door-portal pathfinding

This is the pathfinding design to implement for BPL14 NPCs. It is a hybrid: a cheap cascade of tests, a coarse **door portal graph**, and the existing fine `PathPoly` A* used only to refine the **next hop**. It is not a port of HMLPA*, Recast, JPS, or per-mob LPA* on the navmesh.

## Goal

A mob should reach a specific target (including another mob) by walking through doors it is allowed to use, without hitching the server. Incomplete search is not failure: the NPC may start walking toward the next legal door before the rest of the station is expanded.

## The hybrid in one paragraph

Every tick, try the cheapest test that can still be correct, then stop. If the target is in the same room and line of sight is clear, steer. If the same room is blocked, run existing `PathPoly` A* to the target. If a next door is already committed and still valid, A* only to that door mouth. Otherwise search the **door portal graph** with this mob’s access, and refine only the first hop (PRA*). If several NPCs share the same access class and goal room, one reverse Dijkstra on that graph serves all of them. Rooms are rebuilt only when a moving AI is in them, about to enter, or just got stuck.

```
LOS / same-room steer
        ↓ fail
PathPoly A* in this room (to target, or to committed door mouth)
        ↓ need a new corridor
Access-pruned A* on door portals → PartialPath = next operable door
        ↓ fan-in ≥ 2 (same access-hash, same goal room)
Shared reverse Dijkstra on the same door graph
```

Fine mesh and steering stay. Coarse search is new. Chunk HPA* is not a live third graph; it is a lazy fallback inside huge halls if `NodeLimit` still fires.

## Layers

| Layer | What it is | When it runs |
|---|---|---|
| Cascade filters | LOS, committed-door reuse | Every steering NPC, almost free |
| Coarse | Door portal graph. Rooms are regions; **doors are the abstract nodes** | When the target is in another room (or the committed door died) |
| Fine | Existing `PathPoly` A*, `NodeLimit` 512, 3 ms `PathTime` | Inside the current room only, to the next door mouth (or to the target in the last room) |
| Execution | Existing context steering, bump-open / pry / climb / smash | Always |
| Repair | Stale rooms + door-edge dirty | Occupied / obstruction / bolt-emag, not a station timer |

Do not put moving crates or other mobs on the coarse graph. That is steering and the fine mesh.

## Door portal graph

Flood-fill walkable tiles and **stop at door entities even when those doors are open**. Do not use atmos rooms: they merge when an airlock or firelock opens.

- **Rooms** are regions (id, seed tile, signature of tile count + door set, `Stale` flag).
- **Abstract nodes** are doors (and `PathPortal`s between grids).
- The NPC and the target attach to every door of their current room for the duration of a search.
- Multiple doors between the same two rooms stay distinct (public vs maint).
- Firelocks with no `AccessReader` are free edges.
- Degree-2 vestibules (1-tile airlocks) collapse into a compound edge: the mob needs **both** doors.

Huge doorless halls (arrivals) stay one region. If fine A* in that region hits `NodeLimit`, then and only then add inner waypoints from existing 8-tile pathfinding chunks. Do not maintain chunk portals for every room.

### Access

Do not copy `AccessLists` onto the graph and do not precompute every access subset. Each door is a live `EntityUid`. At expand time call `AreAccessTagsAllowed` with `FindAccessTags(mob)`, and apply bolt / weld / power / emag. Pry and smash remain expensive fallbacks when `PathFlags` allow, matching `NPCSteeringSystem.Obstacles`.

That is hierarchical annotated search: illegal doors are skipped in one predicate, not discovered after expanding command hallway polys.

## PRA* (partial-refining)

The coarse result is a **door sequence**, not a tile polyline.

- Refine **only** the current hop with `PathPoly` A*: mob → door-adjacent tile in this room.
- Last hop: `PathPoly` to the target entity (then existing LOS short-circuit).
- If the committed door is still valid next tick, skip coarse entirely.
- Do not refine six rooms of navmesh up front. The target will have moved; the 3 ms budget should not be spent on tiles the mob will not touch.

`PathResult.Continuing` / `PartialPath` / `NoPath` already exist.

- **PartialPath / Continuing:** next operable door. Walk it. Do **not** increment `FailedPathCount`.
- **Path:** target’s room is reached in the coarse graph.
- **NoPath:** the reachable component under this access is exhausted. Never “I have not expanded medbay yet.”

A hard “one room per frame” cap is a **floor**, not a ceiling. Always expand the current room on the first tick so a next door exists immediately. Then spend remaining `PathTime` toward the target (lookahead: the room behind the committed door). If the door graph is cheap, finish the coarse path in the same tick.

Partial paths from **fine** best-node A* are the wrong hallway: they follow Euclidean heuristic into a locked department. Partial paths from the **door graph** only commit a door this mob can use.

## Per-tick order

1. **Dirty.** Existing chunk dirties (`CollisionChange`, `TileChanged`, relevant moves) mark intersecting rooms `Stale`. Bolt / weld / emag / access config dirties one door edge and drops cached corridors that used it.
2. **Occupied rescan.** If a steering NPC is in a `Stale` room, crossing into one, stuck (`AntiStuck`), or just got `Invalid` / obstacle `Failed` / a blocked committed door: cheap signature check first. Full flood of **that room only** if the signature changed or local A* cannot reach the next door. Occupied heartbeat ~1–2 s as a missed-event backstop. Empty rooms stay stale.
3. **Coarse.** Reuse committed door if valid. Else A* on door portals (or shared reverse Dijkstra). Stop at `PathTime`.
4. **Fine.** `GetPath` to the door mouth (or target). Same `PathTime` slice.
5. **Steer.** Existing mover. A dragged locker is a fine-mesh detour, not a room rebuild.

Topology vs obstruction: walls, doors, and tiles change room identity. Tables, crates, and mobs do not. Rebuilding the room graph for every closet would thrash room ids and drop every cached path through that hallway.

## Sharing (CPU)

| Shared | Not shared |
|---|---|
| One door graph per grid | Fine A* (start pose, sometimes mask/radius) |
| Coarse corridor or reverse Dijkstra keyed by `(access-hash, goal room)` | Steering, DoAfter pry, climb |
| Reachability component per access-hash (instant true `NoPath`) | Lifelong `g`/`rhs` on `PathPoly` |

**Fan-in:** if ≥2 NPCs share access-hash and goal room, build one reverse Dijkstra on the access-pruned door graph and follow it door-to-door. Do not use a Euclidean tile flow field (local minima at locked airlocks). Unique chases stay unique A*.

Combat/chase jobs may consume more of `PathTime` than wander.

## Explicitly out of scope

- METIS or literal HMLPA* on `PathPoly`
- Per-mob LPA* / D* Lite on the fine mesh (start and goal both move)
- JPS / Recast / contraction hierarchies
- Precomputed “all access subsets”
- Station-wide room rebuild on a timer
- Replacing HTN or context steering
- Always-on three-graph HPA* (door + chunk + poly all hot)

Lifelong incremental search, if ever, belongs on the **door graph** after a profiler says repair is the hotspot. Occupied-room flood + edge dirty is the default repair.

## File placement

New types and systems:

- `Content.Shared/BPL/Pathfinding/`
- `Content.Server/_BPL/Pathfinding/` (this document lives here)

Edits to upstream `PathfindingSystem` / `NPCSteeringSystem` use `//BPL` markers. Do not modify `RobustToolbox/`.

## Implementation order

0. **Access-aware `GetTileCost`.** If the poly is a door with `AccessReader`, allow it when the mob’s tags match. Pry/smash stay expensive fallbacks. Crew ID walks an airlock; a mouse does not. Still limited by `NodeLimit` 512. This is useful on the current pathfinder and is not wasted if coarse search changes later.

1. **Door portal graph.** Flood-fill, stop at doors, contract vestibules, `PathPortal` edges. Debug overlay optional.

1b. **Occupied rescan.** `Stale` from existing dirties; flood only occupied / lookahead / obstruction; signature heartbeat on occupied rooms.

2. **Cascade + PRA*.** Wire `RequestPath`: same-room LOS and same-room A* first; else coarse then fine to door mouth. Incomplete ≠ `NoPath`.

3. **Access-class cache + shared reverse Dijkstra** when fan-in ≥ 2. Instant `NoPath` if start and goal rooms are in different components for that hash.

4. **Scheduler** (chase gets more slice). Optional bidirectional coarse A*. Optional smash as extra expensive edges.

5. **Lazy chunk portals** inside a room, only if a real map still blows `NodeLimit` 512 in one region.

## Acceptance (minimum)

- Two-room box: crew ID uses the airlock; no-access does not; pry does with high cost.
- Chase across several airlocks without raising `NodeLimit`.
- Tiny `PathTime`: NPC still walks `PartialPath`; `FailedPathCount` stays 0 until true isolation.
- Weld a girder that splits a room while an NPC is inside: graph splits, NPC replans.
- Drag a locker in front of them: no room-id churn; local detour or another door.
- Bolt a command door: only access-classes that used it replan.
- Two officers, same target room, same access: one shared coarse corridor.
