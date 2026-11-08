# Independent simulation time domains

Status: isolated scheduling logic implemented; RimWorld tick binding and cross-domain world semantics pending.

## Current scheduler behavior

SimulationDomainManager maintains one clock per positive domain ID: faction ID, game tick, selected speed (0 paused; 1x, 2x or 3x), and pending ticks. A single owner thread dispatches tick callbacks at a nominal 60 base ticks per real second per speed. It caps dispatch at 240 callbacks per domain per Advance invocation and retains a backlog; callbacks are sequential and an exception propagates. A successful callback is counted exactly once. Pausing one domain does not pause another. ExportClocks/RestoreClocks serialize metadata for future integration with world saves.

This is purely a clock scheduler. AuthoritativeSimulationHost.Pump can dispatch the per-domain callbacks, but no actual Verse game driver is bound. It does not itself tick Verse maps, simulate quests or validate game-state writes. Real duration, lag and event ordering will require server telemetry and integration tests.

## Global time is not local map time

AsyncWorldTimeComp.DesiredTimeSpeed in the base mod follows active maps. That rule is NOT automatically suitable for an authoritative server with permanently divergent local clocks. Global-world events need their own explicit timeline and consistency rules, not an implicit fastest-map assumption.

## Conservative interaction policy (proposed; not yet implemented)

Until a durable, atomic cross-domain coordinator exists:
- Travel/transfer from A to B while their clocks are incompatible: reject with a client-visible reason; do not duplicate pawns/items.
- A ahead of B: reject stateful interactions that would deliver information or goods from B's future.
- B paused: reject arrivals affecting B; do not force B to tick or silently inject entities.
- Joint battle: require both domains to enter an explicit rendezvous/synchronized battle domain; otherwise reject initiation.
- Trade: use a unique transaction ID, durable reservation of source goods, a coordinated commit, and retry-safe receipts. Until implemented, reject.
- World quest affecting two domains: schedule on a separately governed global timeline; until defined, block activation of unsafe multi-domain consequences.
- Caravan/map generation or pawn migration: all ownership, creation and deletion happen on the server with an atomic, persisted operation ID. Until proven, block.

A speed change affects only a domain the authenticated player's faction may control. No general total ordering exists between different game-time domains; per-domain ticks are not global timestamps.

## Tests and unproven aspects

The console test project exercises 3x versus 1x, pause isolation, duplicate domain rejection and clock export/restore. It does not execute RimWorld, caravans, pathfinding, story events, or real network reconnections. None of the cross-domain policies above is implemented as a transaction coordinator yet.
