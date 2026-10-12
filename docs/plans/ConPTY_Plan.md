# ConPTY console capture — EventStream Slice 4 / Phase 8 shim follow-on `[signed off 2026-08-09 — D7 ContractsVersion stays 4; D3 node-side VT confirmed]`

> The last dark capture path: games that write ONLY to a real console
> (UE WriteConsole — Palworld) currently produce nothing on Windows.
> The shim gains a fourth spawn shape — CreatePseudoConsole — so their
> console text flows into the existing stream as just another source.

## Goal

A ConPTY-spawned Palworld instance shows live console text in the log
viewer, and its parse-rule hits enter the canonical event stream as
`SourceId="conpty"` through the EXISTING parse path — **zero
downstream (Manager/Discord) changes needed; that expectation is
itself the acceptance test** (EventStream plan, Slice 4). Bonus
unlocks per the incident doc: real console → `AttachConsole` +
`CTRL_C_EVENT` graceful stop finally reaches UE (the documented
WM_CLOSE dead end), with a pty-input `0x03` fallback available.

## Status

`[C1–C4a IMPLEMENTED 2026-08-09]` — compile-verified in sandbox
(Contracts + Shim.Protocol + Shim + Node FULL vbc, 0 errors;
PalworldPlugin full compile 0 errors; Manager touched file
reference-partial, zero syntax errors). The VtConsoleFilter passed a
16-case behavioural test suite in-sandbox (CSI/OSC/charset stripping,
CR/CRLF normalisation, sequences split across chunks, UTF-8 split
across chunks, stray BEL/DEL). VS build + the self-tests
(`GSM.Shim --self-test-conpty`, `GSM.Node --shim-conpty-test`) + live
gates G1–G4 are the operator's part — see
claude/ConPTY_Findings_2026-08-09 in the project for test steps.
**C4b (Palworld parse rules) is deliberately NOT written yet**: rules
are authored from LIVE ConPTY captures, which C4a's capture-only run
produces (the log viewer is the collection tool). Implementation
notes / divergences are recorded inline below and in the findings
note.

**Live-fix 2026-08-10 (first Palworld run):** two field findings.
(1) Output arrived minutes late as one giant fused line — conhost
renders a new console row via cursor-positioning CSI (CUP etc.) at
least as often as via a literal newline, and the filter silently
dropped those, so the LineSplitter never saw a break.
VtConsoleFilter now emits ONE LF for row-moving CSI finals (H f E F
B A d), folded with pending CR and suppressed right after a real LF;
21-case test suite passes (node-side only — D3 paying off: plain
GSM.Node redeploy, no shim/game restart semantics change).
(2) CJK mojibake blocks in some lines decode as ASCII byte pairs
read as UTF-16 (慇="Ga") — that is PalServer's own logger writing
narrow bytes through a wide console API; a visible PalServer console
shows the same garbage. Not filtered generically; C4b will decide
whether to unmangle (parse-rule-adjacent, from captures).

**Live-fix 2026-08-11 (ROOT CAUSE, shim-side):** the conpty harness
showed the dummy's output arriving as the SHIM'S OWN STDOUT while
the pty stayed dark — the legacy CreateProcess console rule
duplicates the parent's std handles into a console child when they
aren't console handles and STARTF_USESTDHANDLES is absent (even
with bInheritHandles=False). The node launches the shim with
redirected stdio, so the game's stdio became the shim's pipes; a
console-attached shim (the self-test) dodges the rule, which is why
only the node path failed. Fix in SpawnWindowsConPty: USESTDHANDLES
+ NULL std handles (the Windows Terminal approach). Also added: a
400ms idle flush for quiet partial lines (node-side), pump/session
diagnostics, and a continuous-writer harness dummy (one-shot echo
is a bad pty canary). Shim redeploy required.

---

## Background — why capture is dark today, and where ConPTY fits

- Palworld (UE5 Shipping-Cmd) writes its log via **WriteConsole to a
  real console**, not the stdout handle. Strategy A's redirected pipes
  capture nothing (verified 12 Jul 2026 — only stray CRT/stderr
  asserts). No file log without a mod. Windows capture is therefore
  dark; player data arrived via the Slice 2 REST poller instead.
- `ResolveStrategy` (ProcessManager) maps plugin booleans → SpawnStrategy
  A/B/C. Palworld today forces Strategy B via an **inert
  FileLogSource** (path never exists) purely to get a hidden console.
- The shim (Phase 8-1) already owns all spawns; `SpawnSpec.Strategy`
  is a string; the Supervisor starts pumps only when the game process
  exposes streams. ConPTY slots in as a fourth strategy whose
  "streams" are the pty's input/output pipes: **conhost renders the
  game's WriteConsole into a VT byte stream the shim pumps like
  stdout.** Everything downstream of the pump already exists.
- EventStore.ProcessLine takes `Optional sourceId As String = "stdout"`
  (tailer passes "tailer") — `"conpty"` rides the same parameter.
- Authority (Slice 2, live): `pal-rest` owns PlayerJoin/PlayerLeave.
  Console-parsed duplicates of those must be dropped at append with
  the per-source `Suppressed` counter visible in `/events/sources` —
  the Slice 2 dedupe finally proven against a REAL second source.

---

## Decisions (proposed — confirm at sign-off)

### D1 — New SpawnStrategy `ConPty`, resolved node-side per the ResolveStrategy precedent

`SpawnStrategy` gains `ConPty = 3` (appended; the enum is a node-side
dispatch tag persisted as int in snapshots — no migration, additive
value). The plugin does NOT name strategies: `LaunchOptions` gains the
boolean the contract comment block already reserved —
**`NeedsPseudoConsole`** ("my game writes only via the console API;
give it a console AND capture it"). `StartInstanceRequest` gains the
same boolean (additive wire field, REST ProtocolVersion untouched).

Resolution order (Windows only; ConPTY is meaningless elsewhere):

```
non-Windows              → StdoutCapture (unchanged — Linux stdout works)
StdoutIsLog=True         → StdoutCapture (existing "stdio capture wins" rule)
NeedsPseudoConsole=True  → ConPty
RequiresConsoleIsolation → HiddenConsoleWrapped
has file logs            → HiddenConsoleDirect
else                     → StdoutCapture
```

`CaptureStdout` rule becomes `(Strategy = StdoutCapture OrElse
Strategy = ConPty) AndAlso Not hasFileLogs` — in StartInstanceAsync
AND both adoption paths (snapshot + lean). ConPTY exists to capture;
a plugin that opts in should not also declare file logs (Palworld's
inert one is deleted — D6).

`DisableShim = True` + ConPty: there is no direct-mode ConPTY (the
whole point is the shim owning the pty across node restarts) —
downgrade to `HiddenConsoleDirect` with a warning (yesterday's
behaviour, capture dark, nothing breaks).

### D2 — Shim-side spawn: CreatePseudoConsole, protocol v2 gate

`NativeSpawn.SpawnWindows` dispatches `"ConPty"` to a new
`SpawnWindowsConPty`:

- `CreatePipe` ×2 (pty input, pty output) → `CreatePseudoConsole(size,
  inRead, outWrite, 0, hPC)` → `CreateProcessW` with
  `STARTUPINFOEXW` + `InitializeProcThreadAttributeList` +
  `PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE` +
  `EXTENDED_STARTUPINFO_PRESENT`. No `STARTF_USESTDHANDLES`, no
  inherited handles (the attribute wires the console). Close the
  child-side pipe ends after spawn as usual.
- New `WindowsConPtyGameProcess` (IGameProcess): `StdOut` = pty output
  read end, `StdIn` = pty input write end, `StdErr` = Nothing (a pty
  has no separate stderr — conhost merges). Existing Supervisor logic
  needs NO branch: it already starts pumps only for non-Nothing
  streams, so ConPTY gets a stdout pump + exit watch exactly like A.
- **Teardown ordering (the known ConPTY trap):** the pty output pipe
  does NOT EOF while conhost holds it — `ClosePseudoConsole` must be
  called after game exit for the pump to drain to EOF. So
  `WindowsConPtyGameProcess` exposes the handle and the exit watch
  closes hPC between "game exited" and the existing bounded 3s
  `DrainPumpsAsync`. Dispose also closes hPC (kills conhost) — but
  ONLY the paths that already kill the game reach Dispose; Detach
  never does (the supervisor keeps game + pumps + pty across node
  bounces, which is what makes adopt-with-capture work).
- Size: fixed 512 cols × 200 rows. Wide to minimise mid-line wraps
  (a wrapped long line surfaces as two lines — accepted; the join/
  leave/chat lines that matter are short). No resize support.
- Availability guard: `CreatePseudoConsole` probed via
  GetProcAddress at spawn (Win10 1809+). Absent → SpawnAck failure
  with a clear message (realistically never hits — node baseline is
  far newer).
- **Shim ProtocolVersion 1 → 2** (capability bump per decision 5 of
  the Phase 8 plan — append-only, handshake negotiates min). v2 =
  "understands SpawnSpec.Strategy=ConPty (+ D5's stop kind)". The
  node routes a ConPty spawn ONLY when the negotiated version ≥ 2;
  an older shim (flat/dev deploy — co-located shims normally ship
  with the node) gets the D1 downgrade to HiddenConsoleDirect +
  warning, stamped back onto managed.Strategy/CaptureStdout so
  snapshot and restart stay coherent. Without the gate an old shim
  would silently fall into SpawnWindowsRedirected (its Strategy
  else-branch) — piped, console-less, capture-dark: exactly the
  failure D2 exists to prevent.

### D3 — VT/ANSI stripping is NODE-side; the shim stays a dumb relay

The pty stream is VT: CSI sequences (colors/cursor), OSC title
sequences (`ESC ] ... BEL/ST`), CR games, wraps. The shim pumps RAW
bytes (unchanged pump code, ring included); the node filters before
line-splitting. Rationale: shims are lazy-updated by design (running
shims are never swapped) — stripping fidelity fixes then ride a
plain node redeploy instead of waiting on shim generations; the ring
replay path flows through the same node filter, so adopt catch-up is
cleaned identically; and the raw stream stays available at the
protocol level if a future debug view wants it.

New `VtConsoleFilter` (GSM.Node, stateful across chunks — a sequence
split across two frames must not leak): strips CSI (params to final
byte 0x40–0x7E), OSC (to BEL or ESC \), other ESC two-char forms;
CR and CRLF normalise to LF (a bare-CR spinner line becomes a line
break; empty lines drop later); C0 controls other than LF/TAB
dropped. `ShimSession` applies it to Stdout payloads when constructed
with `filterVt:=True` (set by ProcessManager for ConPty instances),
BEFORE the existing LineSplitter. The existing
`SanitizeCapturedLine` still runs downstream (harmless overlap; it
also covers the direct-A path).

Watch-out: the shim ring trims at 256 KB on frame boundaries, so a
replay can START mid-sequence; the filter must tolerate garbage
before its first ESC (it just passes printable text — one cosmetic
partial line worst-case, same class as today's mid-line trim).

### D4 — Source identity: `SourceId = "conpty"` through the existing path

`CreateShimSession`'s onLine callback passes
`sourceId:=If(managed.Strategy = SpawnStrategy.ConPty, "conpty", "stdout")`
into the SAME `_eventStore.ProcessLine` call. That is the entire
ingestion change: parse rules, replay dedup (source_id is in the
unique index), authority gate, folds, /events, Manager sync, Discord
— all already generic over SourceId. The ring/log-viewer append is
unchanged (clean text = gate G1). `StatePoller`'s reserved-SourceId
rejection list gains "conpty" (additive hygiene).

Authority: nothing to change — PalworldPlugin's pal-rest declaration
already owns PlayerJoin/PlayerLeave, so conpty-parsed joins/leaves
are suppressed at append with counters in `/events/sources` (G3,
live Q4b). ChatMessage etc. remain unowned → conpty appends them
first-come (no REST equivalent — exactly the plan's intended split).

### D5 — Graceful stop: existing path unchanged; pty-0x03 as appended fallback

The slice-5 stop path signals the GAME by PID (`GSM.CtrlCSender`
AttachConsole + CTRL_C_EVENT) — a ConPTY game finally HAS a real
console, so the existing escalation (signal → wait → shim kill →
Shutdown) is expected to work with zero changes and is what G1
verifies. Appended insurance, cheap while we're in the protocol:
`StopMessage.Kind = "pty-ctrlc"` (v2) — shim writes `0x03` to the pty
input pipe (conhost's processed-input converts it to CTRL_C_EVENT for
the attached game). Node uses it only if CtrlCSender proves flaky
against conpty consoles during gates; an old shim's else-branch would
treat the unknown kind as kill, but old shims never host ConPty games
(D2 gate), so the kind is only ever sent where it's understood.

### D6 — Palworld opt-in (plugin 0.4.0) + parse rules

- `GetLaunchOptions`: `NeedsPseudoConsole = True` (Windows path; the
  flag is ignored off-Windows by resolution order).
- `GetLogSources` Windows: DELETE the inert FileLogSource strategy
  hack (NeedsPseudoConsole now states the intent directly; keeping it
  would also flip CaptureStdout off under the D1 rule). Linux
  StdoutLogSource unchanged.
- `GetLogParseRules`: join/leave/chat/lifecycle rules authored from
  LIVE ConPTY captures (C4a runs capture-only first — the log viewer
  is the collection tool; the incident doc confirms join/leave lines
  carry Name/IP/SteamID/CharacterId). Rules emit PlayerJoin/
  PlayerLeave (suppressed by authority — G3's proof), ChatMessage +
  identity/state kinds (genuinely new data).
- Header: `requiresContracts="4"` unchanged (D7), version 0.4.0.

### D7 — Versioning

- **ContractsVersion STAYS 4** (decided at sign-off 2026-08-09):
  `LaunchOptions.NeedsPseudoConsole` is a routine additive member per
  the constant's base rule; Palworld ships bundled with the Manager,
  so the v3/v4 independent-distribution skew rationale doesn't bite.
  A third-party plugin setting the property on an older Manager gets
  a Roslyn error — accepted.
- Shim ProtocolVersion 1 → 2 (D2). Append-only holds: no frame or
  field is removed or repurposed; v1 shims keep working for every
  pre-existing strategy indefinitely.
- REST ProtocolVersion: untouched (additive wire field only).
- Wire-compat: old Manager + new node = no NeedsPseudoConsole field →
  deserialises False → nothing changes. New Manager + old node = field
  ignored → Palworld resolves as today (inert-file removal means it
  falls to StdoutCapture on an OLD node — capture-dark like today but
  strategy A instead of B; acceptable for the mixed window, noted in
  C4).

### D8 — Linux: no-op, deliberately

`ResolveStrategy` non-Windows returns StdoutCapture unchanged — on
Linux the same games write to real stdout (Palworld verified path:
StdoutLogSource). A Linux PTY equivalent (openpty for a game that
insists on isatty) has no known customer; deferred, not designed.

---

## Touch-points

**GSM.Contracts**
- `IGamePlugin.vb`: `LaunchOptions.NeedsPseudoConsole As Boolean = False`
  (+ doc comment mirroring the existing block's promised future bool).
- `NodeApiContract.vb`: `StartInstanceRequest.NeedsPseudoConsole`
  (additive); ContractsVersion untouched (D7 — add a line to the v4
  comment recording the no-bump call).

**GSM.Manager** (upstream launch plumbing only — no event consumers)
- `InstanceManager.StartInstanceAsync`: copy the new LaunchOptions
  boolean onto StartInstanceRequest (one line beside StdoutIsLog /
  RequiresConsoleIsolation).

**GSM.Shim.Protocol**
- `Protocol.vb`: ProtocolVersion 2; SpawnSpec.Strategy doc gains
  "ConPty"; StopMessage doc gains "pty-ctrlc". (No new frames/fields.)

**GSM.Shim**
- `NativeSpawn.vb`: `SpawnWindowsConPty` + `WindowsConPtyGameProcess`
  + CreatePseudoConsole/ClosePseudoConsole/attribute-list interop.
- `Supervisor.vb`: exit watch closes the pty before the drain (via a
  new optional IGameProcess hook, e.g. `PrepareForDrain()` default
  no-op); `"pty-ctrlc"` stop kind. Pumps untouched.
- Self-test: `--self-test-conpty` (spawn cmd.exe /c marker under a
  pty → marker arrives through the pump → stop → Exited).

**GSM.Node**
- `ProcessManager.vb`: SpawnStrategy.ConPty; ResolveStrategy (+ param
  from request); CaptureStdout rule ×3 sites (start + both adopts);
  StartViaShimAsync version-gate downgrade (D2); CreateShimSession
  sourceId + filterVt; strategy-name log string ("D:ConPty").
- `ShimSession.vb`: optional `filterVt` → new `VtConsoleFilter`
  applied to Stdout payloads pre-LineSplitter.
- `VtConsoleFilter` (new file or in ShimSession.vb): stateful VT
  stripper per D3.
- `StatePoller.vb`: reserve "conpty" SourceId.
- `--shim-conpty-test` harness (mirrors --shim-reconnect-test): spawn
  a VT-emitting child under ConPty → clean lines out → detach →
  adopt → replayed marker still clean → stop.

**GSM.PluginsSource**
- `PalworldPlugin.vb`: D6.

**Untouched by design:** NodeProgram.vb reloadOnChange block; all
/events consumers; EventSyncService; Discord; snapshot schema (Strategy
already an int column); crash policy; adoption flow shapes.

---

## Slices (confirm-gated, in order)

### C1 — Contracts + resolve plumbing `[implemented]`
D1 + D7 + the Manager copy line + CaptureStdout rule + snapshot/adopt
passthrough. No shim, no behaviour change (no plugin sets the flag
yet; ConPty resolution routes to StartViaShimAsync which would
downgrade against the v1 shim — dormant until C2/C3).
*Verify:* full vbc compile Contracts + Node in sandbox; Manager
touched files reference-partial (async audit by hand — BC36943 is
invisible to partial compiles).

### C2 — Shim ConPTY spawn + protocol v2 `[implemented — self-test yours]`
D2 + D5's stop kind + `--self-test-conpty`.
*Verify:* Shim.Protocol + Shim full compile; user runs
`GSM.Shim --self-test-conpty` on Windows.

### C3 — Node conpty wiring `[implemented — harness run yours]`
D3 + D4 + the D2 version gate + `--shim-conpty-test`.
*Verify:* Node full compile; user runs `GSM.Node --shim-conpty-test`
(spawn → clean lines → detach/adopt → replay clean → stop). This is
the adopt-continuity proof BEFORE a real game is involved.

### C4 — Palworld opt-in + parse rules `[C4a implemented; C4b awaits live captures]`
- **C4a — capture only:** plugin 0.4.0 with NeedsPseudoConsole, inert
  file source deleted, rules still empty. Live: G1.
- **C4b — parse rules** authored from C4a's captured lines. Live:
  G2 + G3.
*Verify:* plugin compiles via sandbox vbc reference-partial + VS/
runtime Roslyn load; live gates below.

---

## Gates (live, on the win-test node)

- **G1 (C4a)** — ConPTY-spawned Palworld shows live console text in
  the log viewer; graceful stop works (save completes; exit clean,
  not force-kill); kill the exe → crash-restart works and the restart
  is ConPTY again (Strategy persisted); restart the node service mid-
  session → re-adopt: same game PID, console text resumes with ring
  replay (Q1/Q3-class regression checks). Also: kill the SHIM →
  game must survive (no-worse-than-A rule; expect console writes to
  fail silently or be tolerated — observe, don't assume).
- **G2 (C4b)** — join/leave/chat activity → parse hits append with
  `sourceId=conpty` (`/events?since` shows them); Manager History/
  Discord pick them up through the EXISTING sync with zero Manager/
  Discord code changes (the acceptance test).
- **G3 (C4b)** — authority: pal-rest still owns PlayerJoin/PlayerLeave;
  `/events/sources` shows the conpty source's `Suppressed` counter
  climbing on join/leave lines while ChatMessage appends normally —
  Slice 2's dedupe proven against a real second source (live Q4b).
- **G4 (regression)** — LO/Factorio (A/B/C) start, stop, crash-restart,
  node-restart adopt unchanged; `--shim-reconnect-test` still passes
  (v2 handshake, v1-frame compatibility).

---

## Risks / watch-outs

- **Console lifetime now couples to the shim harder than Strategy B.**
  B gives the game its own conhost; ConPTY's conhost is the shim's
  child holding the pty. Shim crash → conhost dies → the game's next
  WriteConsole fails. Expected UE behaviour is tolerate-and-continue
  (log write failure), making this the same residual class as
  Strategy A's broken pipe — but it is UNVERIFIED until G1's
  kill-the-shim check. Do NOT add job-object/PDEATHSIG coupling to
  "fix" anything here (standing rule: never bind game life to shim
  life).
- **ClosePseudoConsole ordering** (D2): close before drain on exit or
  the pump never EOFs and every exit eats the 3s drain timeout; close
  while the game lives and you've killed its console. The exit watch
  is the ONLY caller on the live path; Dispose covers teardown.
- **VT fidelity is empirical.** The filter will be wrong somewhere
  (UE progress bars, OSC titles, exotic sequences). Node-side
  placement (D3) makes fixes cheap; G1 eyeballs real output early.
- **512-col wraps**: long lines split in two. Parse rules must anchor
  on line PREFIXES (UE log category tokens) and accept truncated
  tails; don't write rules that need the full width of a wrapped line.
- **Old shim + ConPty spawn** = capture-dark redirected spawn — the
  D2 version gate is load-bearing; keep it in StartViaShimAsync, not
  in ResolveStrategy (resolution happens before any handshake).
- **Protocol discipline**: v2 is append-only — new enum string values
  + one stop kind, zero removed/repurposed fields. Never let a
  ConPTY-era refactor touch v1 frame semantics.
- **Two PIDs per instance** unchanged: game PID from CreateProcessW
  under the pty; conhost is an implementation detail (never tracked,
  never reported; lives under system32 so the orphan reaper's
  image-path scoping ignores it — verify in G1's reap-adjacent checks
  if a crash-restart fires).
- **BC36943 / partial-compile blindness** (Slice 3 findings): audit
  every new Await by hand — none inside Catch/Finally/SyncLock;
  VS build remains the Manager gate.
- **Palworld mixed-window** (D7): new Manager + old node loses the
  inert-file Strategy-B forcing (falls to A, capture-dark, harmless).
  Fleet co-updates anyway; noted so it isn't chased as a bug.

## Deferred / non-goals

- Linux PTY equivalent (D8) — no customer.
- Raw-VT debug surface (per-instance-gated LogLine kind or raw view) —
  the stream keeps semantic events only, per the EventStream plan.
- ConPTY for LO/Factorio — A/B/C already capture them; uniform-ConPTY
  has no payoff and B/C's AttachConsole stop story is proven.
- Authority flip (conpty over pal-rest for joins) — the one-line
  plugin change the EventStream plan describes; only if console
  fields prove richer AND patch-stable.
- Resizable pty / configurable dimensions.

## As-built notes (2026-08-09, C1–C4a)

- Supervisor needed ZERO pump/serve changes, as predicted — the only
  shim-side hook is a new `IGameProcess.PrepareForDrain()` (no-op for
  pipe spawns; the ConPTY process closes its pseudoconsole there),
  called by WatchExitAsync between game-exit and the bounded drain.
- The D2 gate lives in ShimSession.StartAsync (new optional
  `requireProtocolVersion` param, checked after the handshake, BEFORE
  Spawn); StartViaShimAsync passes 2 for ConPty and on the
  "shim protocol" failure downgrades managed.Strategy →
  HiddenConsoleDirect + CaptureStdout=False and retries once.
- VtConsoleFilter is a new file (GSM.Node/VtConsoleFilter.vb),
  byte-level state machine (Normal/Esc/EscInter/Csi/Osc/OscEsc +
  pending-CR); it never removes bytes ≥ 0x80 so split UTF-8 chars
  survive. Sandbox-tested with 16 behavioural cases incl.
  chunk-straddling sequences.
- ConPTY interop uses a second P/Invoke entry (`CreateProcessExW`,
  EntryPoint:=CreateProcessW) taking STARTUPINFOEXW — the existing
  STARTUPINFOW import is untouched. PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE
  = &H20016; availability guarded by EntryPointNotFoundException →
  clear SpawnAck error.
- ManagedInstance gained no ConPTY fields — Strategy (int, persisted)
  carries everything; both adopt paths + crash-restart work off it.
- Also in this batch (separate plan, ordered first per sign-off):
  node-boot resurrection — docs/plans/NodeBoot_Resurrection_Plan.md.

## References

- `docs/plans/EventStream_Unification_Plan.md` — Slice 4 stub, Source
  authority, ingestion sources.
- `docs/plans/Phase8_Plan.md` — shim architecture, decisions 5/6/9/11,
  watch-outs (append-only protocol, two PIDs, no life-binding).
- `docs/plans/Phase8-3_Plan.md` — HelloAck echo precedent, sweep/lean
  adopt (conpty instances lean-adopt like any A instance).
- `claude/Palworld_Incident_2026-07-28` (project) — WriteConsole
  finding, follow-up #1, exe family.
- `GSM.Shim\NativeSpawn.vb`, `GSM.Shim\Supervisor.vb`,
  `GSM.Shim.Protocol\Protocol.vb`, `GSM.Node\ShimSession.vb`,
  `GSM.Node\ProcessManager.vb` (ResolveStrategy ~1660,
  CreateShimSession ~1496, CaptureStdout sites ~298/866/1040,
  SanitizeCapturedLine ~2039), `GSM.Node\EventStore.vb`
  (ProcessLine ~801), `GSM.PluginsSource\PalworldPlugin.vb`
  (GetLogSources ~305, GetLaunchOptions ~403).
