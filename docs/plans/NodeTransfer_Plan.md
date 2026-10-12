# Node Transfer & Multi-Manager — Plan (2026-08-15)

Status: PLANNED — not started. Sequenced AFTER the ConPTY/resurrection live gates and Diagnostics Wave 2. Origin: operator asked whether a second Manager could discover a node's installations/instances and whether two Managers conflict (answer: no discovery path exists, and yes — rules-push flapping, double automation/notifications, boot-flags/forget races, node-update races, shared token). Full remoting (thin-client Manager executing foreign plugin DLLs) was considered and REJECTED: executing DLLs fetched from another Manager is a built-in malware channel, and collectible-AssemblyLoadContext isolation plus contracts-version skew buys pain for a need the ladder below covers without shipping code.

## Decision summary

One-Manager-per-node stays the invariant — but it becomes *transferable*, *observable*, and *enforced* instead of assumed. Three independent rungs, each additive and independently shippable:

## Rung 1 — Node export/import bundle (sequential ownership handoff)

Piecemeal today is impossible: the Manager DB is the only home of the Nodes → Installations → Instances model (node snapshots hold run-state only, and there is deliberately no node-side enumeration of the Manager model). Whole-DB transplant is all-or-nothing. The bundle makes single-node moves first-class.

**Bundle contents** (.pgsmnode, one node's DB subgraph from Manager A):
- NodeEntity: address, port, auth token, TLS pin, flags.
- Its Installations + their Instances: ConfigJson, exe overrides, boot flags, ordering, port assignments.
- Referenced extras: Steam credential entries the installations point at; shared-config group memberships (OPEN DECISION: copy the group vs split it when it spans nodes); automation rules scoped to the transferred instances.
- OPTIONAL History/PlayerActivity slice keyed by the node's session identities (OPEN DECISION: transfer vs archive-on-A; default archive — B starts History fresh, A keeps the past read-only).

**Security (the bundle is the real new attack surface — it carries the node token):**
- Encrypted at export (operator passphrase, entered at import). Treat as a key file.
- **Token rotation on import**: importing Manager immediately re-keys the node over the authenticated channel — retroactively invalidates the exported copy AND the old Manager's lingering credentials. Makes the bundle one-time-use. (Same hygiene applies after any suspected Manager-DB compromise — a copied DB is otherwise a perpetual skeleton key.)
- Optional short validity window stamped at export.

**The missing primitive: detach-without-forget.** A's delete flows call node-side /forget — exactly wrong for transfer (instances must keep living: snapshots, parked autostart rows, flap guards stay). Export flips A's copy to a read-only "transferred" state (or local-delete WITHOUT the node call). Export marks A detached at package-creation time; B activates on import — ownership is sequential, so the concurrency conflicts never arise.

**Import preconditions (checked, not assumed):** B has every GameId in the bundle at SAME-OR-HIGHER plugin version, and contracts ≥ the bundle's (operator's rule; ConfigJson is opaque key/value so higher plugin versions read older config exactly as they already do across upgrades). Conflict checks: duplicate node address, port-range collisions on B, credential-id collisions. Per-installation warnings, not blind import.

**Node-side changes: ZERO** for rung 1. B connects with the transferred token+pin; the event stream already tolerates a new consumer (per-Manager cursors; first-seed lifecycle backfill catches the handoff moment's start/stop).

## Rung 2 — Observer mode (concurrent eyes, one driver)

Per-node Manager-side flag: this node is VIEW-ONLY for this Manager. Disables rules push, automation, notifications, boot-flags/park/forget writes, node self-update; keeps status polling, event-stream consumption, log viewing, History building, Diagnostics. The stream side is ALREADY multi-consumer-safe by design — observer mode is flag-checks at the handful of side-effectful call sites. Gives a second operator seat / NOC view with zero coordination protocol.

## Rung 3 — Ownership lease (enforced single writer, cheap switching)

Small additive NODE endpoint: ownership record (manager-id, TTL, renewed by owner; readable by authenticated Managers only — no anonymous reads, it would leak fleet topology). Managers check the lease before side-effectful ops; a non-owner auto-degrades to observer behaviour. Lease transfer = the ownership handoff moment (reuses rung 1's bundle for the metadata). Stale-lease takeover (owner Manager box died) = the disaster-recovery story, for free.

**Security analysis (operator's question: "does turning off my Manager open an attack?"):** No new door. Lease claims ride the node's authenticated API (Bearer + pinned TLS) like everything else — a party that can claim a stale lease already holds the node token and can already fully control the node today, Manager online or not. The lease only arbitrates between already-authenticated Managers.

## Explicitly rejected

- **Thin-client remoting with plugin DLL transport** (ALC-isolated execution of the remote Manager's plugins): possible in .NET 8 (collectible ALCs unifying GSM.Contracts to the host copy, requires host contracts ≥ every remote's), but it's remote-code-execution by design if any remoted Manager is compromised, plus version-skew and ALC edge cases. If a remote-view Manager is ever wanted beyond rung 2, the shape is a Manager↔Manager API serving DECLARATIVE render-models (field/action descriptors — the plugin API is already substantially declarative), never code.
- **True concurrent writing** (two Managers driving one node simultaneously): nothing makes it cheap; the lease makes not-having-it safe instead of accidental.
- **Coordination via utility plugin on both Managers**: utility plugins have no hooks to stand down the host's rules push/automation/lifecycle writes — detection without the power to yield. Wrong layer.

## Rules honored

VB.NET; contracts additive only; wire-compat on existing endpoints (rung 1: zero node changes; rung 3: one new authenticated endpoint); no version bumps needed under the established INodeClient-additive precedent; NodeProgram reloadOnChange untouched.
