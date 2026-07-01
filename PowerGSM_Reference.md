# PowerGSM Reference

Current-state reference for PowerGSM — how the shipped system actually
works, subsystem by subsystem. This is the index; the material itself
lives in the `reference/` files listed below.

This reference set is descriptive (how things work today), distinct from
its sibling docs:

- **`ROADMAP.md`** — forward-looking: what's planned, current focus, phase ordering.
- **`CHANGELOG.md`** — Keep-a-Changelog history of what shipped, accumulating under Unreleased until a release tag.
- **`Backlog.md`** — deferred ideas not yet scheduled into a phase.
- **`PhaseXX_Plan.md`** — per-phase design specs (the detailed plan for one phase).
- **`reference/`** (this set) — current-state reference and hard-won how-to.

## The reference set

| File | Covers |
|---|---|
| [`reference/build-and-project.md`](reference/build-and-project.md) | Project settings, build order (build PHASES 1–6 — a build sequence, not roadmap phases), dependency map, known-harmless warnings, versioning & release. |
| [`reference/vbnet-gotchas.md`](reference/vbnet-gotchas.md) | The VB.NET quick-reference table plus accumulated language pitfalls gathered from across the build. |
| [`reference/plugins.md`](reference/plugins.md) | The Roslyn-compiled plugin model and the Manager-side utility plugins (lo-myrealm and friends). |
| [`reference/identity.md`](reference/identity.md) | Last Oasis identity propagation (5g-2 / 5g-2b), Conan specifics, and connection bindings. |
| [`reference/node.md`](reference/node.md) | GSM.Node: the wire API, file operations, node auth, the GSM.NodeSetup tool, prerequisite checks, and Linux log tailing / SSE. |
| [`reference/manager.md`](reference/manager.md) | The WinForms control plane: config & editor UI, live state, History, the Phase 4C file-management surface, Phase 5m resilience, and the 5l self-update flow. |
| [`reference/automation-core.md`](reference/automation-core.md) | The automation engine: contracts/data, polymorphic rule JSON, RestartCoordinator, materializer, the version-mismatch trigger, and the NotifyAction transport. Holds the cumulative automation file map. |
| [`reference/automation-ui.md`](reference/automation-ui.md) | The automation forms: EditInstanceForm restart schedule, InstallationPanel reorder UI, tree-state preservation, the non-modal AutomationRulesForm, and the RuleEditorForm rewrite (tabbed layout, sequence editor). |

## What goes where (filing rules)

When adding new reference material, file it by subsystem, not by the
phase that produced it:

- A VB.NET language trap that bit during any phase → **vbnet-gotchas.md**, even if the feature itself is documented elsewhere.
- Build/project/dependency/versioning facts → **build-and-project.md**.
- Anything about the headless node (endpoints, on-disk ops, setup, prereqs, log streaming) → **node.md**.
- Manager WinForms surfaces (panels, forms, history, file management, resilience, self-update) → **manager.md**.
- The automation rule engine, coordinator, serializer, triggers, transports → **automation-core.md**; the rule-authoring forms and tree UI → **automation-ui.md**.
- Game-identity propagation and connection bindings → **identity.md**.
- The plugin model and individual utility plugins → **plugins.md**.

Keep entries current-state. A bug's full root-cause narrative belongs
here once; the one-line "what shipped" belongs in CHANGELOG.md, and the
design rationale belongs in that phase's PhaseXX_Plan.md.

> History note: this file was a single appended-by-phase monolith
> through the initial 6-phase build and its post-phase additions. It was
> re-sorted into the subsystem set above once the phase-chronological
> ordering stopped scaling. The re-sort was lossless — every original
> section moved verbatim into exactly one file.
