# Manager Diagnostics window — Plan (2026-08-14)

Motivation (operator): "why not build in troubleshooting tools straight into manager so that we don't have to do all this curl song and dance?" — G3-style checks currently require curl against node endpoints; watermark/trace debugging requires node-box CLI. Goal: a Tools → Diagnostics window that makes the console-pipeline and fleet state observable from the Manager.

## Multi-node model

Tree on the left: Fleet root → node → instances (from GsmDbContext: Nodes → Installations (NodeId) → Instances (InstallationId)). Detail panel on the right is scope-dependent: fleet/node selection shows the PROBE GRID (coalesced fleet view), instance selection shows per-instance tabs. Everything 404-tolerant: an older node renders "not supported by this node" per tab, never an error dialog.

## Wave 1 (Manager-only — works against currently deployed nodes)

1. **Probe grid** (fleet + node scope): one row per (attached) node — reachable, latency ms, Build, ProtocolVersion, ContractsVersion, Platform, EventStreamSupported, TLS on/off. "Probe All" re-runs GetApiVersionAsync(force:=True) fleet-wide, concurrently. Detached nodes listed greyed, not probed. Catches mixed-version fleets (the recurring caveat class) at a glance.
2. **Event sources tab** (instance scope): grid of /events/sources — SourceId, Appended, Suppressed, OwnedKinds — plus Refresh. This is gate G3 as a click.
   - Plumbing: INodeClient.GetEventSourceDiagnosticsAsync(instanceId) (ADDITIVE member, same no-version-bump rationale as UpdateParseRulesAsync/UpdateBootFlagsAsync — Manager↔Node plumbing, not plugin-facing; EventSourceDiagnostics DTO already exists in Contracts) + NodeHttpClient GET /api/instances/{id}/events/sources with WrapException.
3. **Stream health tab** (instance scope): EventSyncService.GetState (Inactive/Pending/Active), legacy-suppression flag, sync cursor (settings key eventsync:cursor:{id}), node id/address. Manager-local data only.
4. **MainForm**: Tools → "&Diagnostics..." opens the non-modal window (HistoryWindow show-without-owner pattern).

## Wave 2 (node endpoints + tabs — Node AND Manager rebuild)

5. **Watermarks tab**: GET /api/instances/{id}/events/watermarks — endpoint-ifies --dump-watermarks (purge watermarks per kind, replay-dedup state, snapshot ShimRingSeq). CLI stays for node-box use.
6. **ParseTrace tab**: node keeps a small per-instance in-memory ring of recent ParseTrace entries (today: 20 lines logged then disarm); GET /api/instances/{id}/events/parsetrace returns the ring; POST .../parsetrace/rearm re-arms. UI: grid + Re-arm button.
7. **Rule dry-run**: POST /api/instances/{id}/events/test-line { line } → runs the line through the instance's REGISTERED rules (no append, no fold) → per-rule match verdict + captured groups. UI: textbox + results grid. (The G2 prefix bug would have been a 30-second check.)
8. **Boot/resurrection tab**: GET /api/instances/{id}/boot-state → snapshot row presence/State/parked flag, boot flags, BootResurrectAttempts chain (flap guard). Makes GR gates and "why didn't it autostart" observable.
9. **Shim link tab**: GET /api/instances/{id}/shim-state → strategy, shim protocol version, attached/linked, ring FirstSeq/NextSeq, node ConsumedRingSeq, last-frame-at.

All Wave 2 endpoints additive; Manager tabs degrade to "not supported" on 404 (NodeApiException StatusCode=NotFound).

## Explicitly out of scope

- Remote triggering of node self-tests (--shim-reconnect-test etc. spawn processes — operator runs those on the node box).
- Arbitrary request sender ("curl with extra steps"); the probe grid + typed tabs cover the sanctioned uses.

## Rules honored

VB.NET; no filesystem watchers; contracts additive only (INodeClient member additions follow the established no-bump precedent); wire-compat on existing endpoints (Wave 1 adds ZERO node changes; Wave 2 endpoints are new paths only); NodeProgram reloadOnChange untouched.
