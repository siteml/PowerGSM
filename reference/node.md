# PowerGSM Reference — Node

Part of the PowerGSM reference set (index: [`../PowerGSM_Reference.md`](../PowerGSM_Reference.md)).
The Node is the headless ASP.NET Core service that runs game-server
processes: process spawning and lifecycle, log tailing, declarative log
parsing (EventStore), installation / update (SteamCMD, redist, archive
extraction), host prerequisite checks, node-side file operations,
authentication hardening, the GSM.NodeSetup companion tool, and the
cross-platform (Windows / Linux) hardening work. The Manager-side
consumers of the Node's wire API are in [`manager.md`](manager.md);
player-identity resolution across the boundary is in
[`identity.md`](identity.md); node-relevant VB.NET pitfalls are in
[`vbnet-gotchas.md`](vbnet-gotchas.md).

> Note: the "PHASE 7 — GSM.NodeSetup" heading below is a build-order label
> for the companion tool, not roadmap Phase 7.

---

### Log file tailing on the node

`StartInstanceRequest.LogFilePaths` lists absolute paths the node
should tail and merge into the instance log buffer. Plugins declare
file log sources via `GetLogSources`; Manager substitutes
`{InstallPath}` / `{InstanceId}` tokens in `FileLogSource.PathPattern`
and sends resolved paths on start. Tailer in `ProcessManager`:

- Open-read-close pattern per 500ms poll cycle (no persistent FileStream)
- `FileShare.ReadWrite Or FileShare.Delete` so the game's exclusive
  write doesn't conflict
- Resumes from a persisted byte cursor when the file's first-bytes
  fingerprint matches the last saved one (see "Tailer position
  cursor" below); otherwise falls back to a size-based heuristic
  on first open: read from 0 if file is small (<2MB), backfill
  last 512KB if large
- 5-second startup delay after file exists
- Handles truncation (position reset to 0 if file shrinks)

When `LogFilePaths` is populated, stdout capture is skipped for the log
buffer (file is the authoritative source) but stdout pipes are STILL
drained via `BeginOutputReadLine`/`BeginErrorReadLine` — unread redirected
pipes will block UE4 writes after ~4KB and hang the server. The handlers
just discard data when `captureStdout = False`.

---

### Declarative log parsing (EventStore)

Plugins implement `GetLogParseRules()` returning a list of
`LogParseRule` objects with regex patterns (using named capture groups)
and a `ParsedEventKind` classifier. The rules are sent to the node in
`StartInstanceRequest.LogParseRules`.

Node's `EventStore`:
- Compiles rules once per `RegisterInstance` call (on instance start)
- Applies them to every log line flowing through stdout capture or
  file tailer
- Updates in-memory per-instance player list and server state (thread-safe)
- Persists chat messages to SQLite (`chat_messages` table)
- Exposes data via `/api/instances/{id}/players`, `/server-state`, `/chat`

Node tracks all of this independently — a Manager can connect at any
time and see current state without having been running during the
events.

Supported capture group names (match = populates the named field on
PlayerSession/ServerStateResponse/etc.):
`DisplayName`, `PlatformPersona`, `PlatformUserId`, `CharacterId`,
`RemoteAddress`, `Message`, `MatchState`, plus `Custom_*` groups
(passed through into event metadata).

`ParsedEventKind` values: `PlayerJoin`, `PlayerLeave`, `PlayerIdentity`,
`ChatMessage`, `ServerStateChange`, `TileLoaded`, `Custom`.

---

### SteamCMD integration

Root-cause findings documented for future reference:

- **Trailing backslash in install path** corrupts SteamCMD arg parsing:
  `"C:\GameServers\"` becomes `\"` interpreted as escaped quote,
  swallowing `+login`, `+app_update`, `+quit`. Always strip trailing
  backslashes before quoting.
- **Self-update pre-pass** with NO stream redirection first (loop until
  exit code 0), then redirected install pass. Using redirected streams
  for self-update poisons subsequent launches.
- **Do not use `WaitForExitAsync`** with redirected stdio — deadlocks
  waiting for streams to close. Poll `HasExited` in a `While` loop.
- **Steam Guard flow (exit code 5)**: detect, set operation state to
  `WaitingForInput`, await TCS; Manager polls, prompts user, POSTs code
  via `/api/install/{id}/prompt`; retry loop relaunches SteamCMD with
  `+set_steam_guard_code CODE` prepended.
- **Exit code 7 after success** = SteamCMD self-updated post-install.
  Treat as success if manifest file exists.
- **Send empty newline to stdin every 20s** unconditionally — keeps
  SteamCMD from hanging in interactive mode on some commands.

---

### Common redist installation

`InstallRunner.RunCommonRedistAsync` runs after successful SteamCMD
install on Windows. Enumerates `_CommonRedist\**\*.exe` and runs each
with silent flags:
- `dxsetup.exe` → `/silent`
- `vcredist*` / `vc_redist*` → `/install /quiet /norestart`
- Everything else → `/quiet /norestart`

Success codes: 0, 1638, 3010. 5100 = system requirements not met (skip).
5-minute timeout per installer. Only runs on Windows. Node process must
run with sufficient privileges (administrator typically required).

---

### Archive extraction

Two-tier extraction strategy in `InstallRunner.
ExecuteDownloadStepAsync`:

1. **Native `tar` (primary, Linux/macOS only)** — for `.tar.xz`
   / `.txz` archives, the extractor shells out to the platform's
   native `tar` binary via `ProcessStartInfo.ArgumentList` (each
   arg as a separate argv entry, so spaces or special chars in
   paths don't need quoting). tar handles every variant of
   long-name extension correctly: POSIX Pax, GNU `LongName` /
   `LongLink`, BSD-tar's variant. Preserves unix file modes.
   `--strip-components=1` collapses a single-top-level wrapper
   directory in one flag, replacing the manual staging-and-hoist
   pass. `HasExited` polling rather than `WaitForExitAsync`
   because the latter deadlocks on redirected streams in .NET 8
   (same pattern as `RunSteamCmdProcessAsync`).

2. **`SharpCompress 0.36.0` (fallback)** — used for `.zip` (via
   the built-in `System.IO.Compression.ZipFile`), Windows
   `.tar.xz`, and `.tar.gz` / `.tgz` / `.7z` / `.rar` (via
   `ArchiveFactory.Open`). Windows `.tar.xz` is in scope only
   because no current plugin produces a Windows direct-download
   case; if one materialises, modern Windows (10 1803+) ships
   bsdtar at `%SystemRoot%\System32\tar.exe` and the same
   shell-out pattern drops in. Fallback path includes:

   - **Pax-header filtering.** SharpCompress treats BSD-tar's
     `@PaxHeader` entries as regular files; `IsPaxHeaderEntryKey`
     filters entries whose path segments match `PaxHeader`,
     `@PaxHeader`, `PaxHeaders*`, or `@PaxHeader*` so they don't
     leak as junk files. Doesn't help long-name resolution —
     SharpCompress doesn't process the long-name records for
     this variant either, which is why the native-tar branch
     exists.
   - **Strip-top-level via staging+hoist.** Extract to
     `<install>/.gsm-staging/`, detect a single top-level
     directory in staging, hoist its contents up via
     same-volume `Directory.Move` / `File.Move` (O(1) metadata
     ops, near-free). `MergeDirectoryRecursive` handles the
     update flow where target directories already exist.
   - **Unix mode preservation via `ApplyUnixModeIfNeeded`.**
     `WriteEntryToDirectory` doesn't apply tar entry modes;
     the runner snapshots `entry.Mode` before extraction and
     calls `File.SetUnixFileMode(destPath, mode And &HFFF)` on
     Linux/macOS afterward (lower 12 bits to round-trip
     suid/sgid/sticky if set). No-op on Windows where
     `UnixFileMode` would throw `PlatformNotSupportedException`.
     Same shim hasn't been wired into the
     `ArchiveFactory.Open` (tar.gz / 7z / rar) path because no
     plugin currently produces those — tracked as a follow-up.

Download validation rejects files under 1KB as likely error
pages. HttpClient has `AllowAutoRedirect=True` and a
`User-Agent: PowerGSM/1.0` header (factorio.com rejects requests
without a UA).

---

### Top-level wrapper directory handling (`StripTopLevelDirectory`)

Many release tarballs wrap every entry under a single top-level
directory (`factorio_2.0.76.tar.xz` → all entries under
`factorio/`). Without stripping, the install lands a level too
deep — plugin-relative paths like `bin/x64/factorio` resolve
against `<install>/factorio/...` instead of `<install>/...`,
and version-detection reads of `data/base/info.json` miss the
file entirely.

Plugins opt in via `DownloadFileStep.StripTopLevelDirectory =
True`. The native-tar branch implements it with
`--strip-components=1`. The SharpCompress fallback extracts to
a staging directory under `<install>/.gsm-staging/`, then
`HoistStagedContents` checks for a single top-level subdirectory
and promotes its contents up to the install root via same-
volume `Directory.Move` / `File.Move`. Multi-top-level archives
(the flag was set but the archive doesn't actually have a
single wrapper dir) extract everything to the install root
as-is — the flag is a request, not a guarantee.

Two sibling fields (0.5.0):

- `ExtractToRelativePath` — extract into an install-root
  subdirectory instead of the root (SDV's mod zip → `Mods\`),
  with a `GetFullPath`+`StartsWith` traversal guard; composes
  with StripTopLevelDirectory (the hoist target becomes the
  subdir). Older nodes deserialize-and-drop the field and
  extract to the root — diagnosed live when a stale node
  binary scattered the SMAPI installer across the install
  root.
- `ExtractOnlyPaths` — allowlist of entry paths (forward
  slashes, case-insensitive) to extract; every branch (zip
  entry loop, tar.xz stream, ArchiveFactory) filters through
  `NormalizeEntryKey` and STOPS as soon as every listed entry
  is on disk. Motivating case: mesa-dist-win's ~1 GB 7z where
  SDV needs exactly two DLLs — minutes down to seconds.

The download loop also reports byte progress
(`op.BytesDownloaded/BytesTotal`, 500ms-throttled
"Downloading f: X / Y MB" message; 0–80% of the step when an
extraction follows, 0–99% otherwise) and extraction reports
per-entry progress ("Extracting: N / M files", 80→99% span;
count-only for the streaming tar reader where the total is
unknown).

Update flow lands in `MergeDirectoryRecursive`, which overlays
the new files onto the existing install directory tree. Files
at conflicting paths are overwritten; subdirectories are
recursively merged. Same-volume `File.Move(... overwrite:=True)`
is O(1) metadata, so even a multi-GB update finishes the hoist
step in well under a second.

---

### Direct-download update flow

`FactorioPlugin.GetUpdateSteps` previously had a `SteamCmd`
branch only; `DirectDownload` fell through to an empty step
list. The runner executed zero steps and recorded "completed
successfully" with no Download/Extract/Configure entries
between the bookends — a silent no-op that left the install
frozen at whatever version it was at on first install.

The direct-download update branch now emits the same
`DownloadFileStep` (with `StripTopLevelDirectory = True`) the
install path uses; the URL auto-redirects to whatever's
current on the chosen channel, so the same URL produces the
new version on update. Saves and mods live under `saves/` and
`mods/` which aren't in the tarball, so they survive the
overlay.

The `BuildConfigPathStep` now runs on update too. SteamCmd's
`app_update` rewrites `config-path.cfg` to its upstream
defaults on every update; the headless tarball ships its own
`config-path.cfg` and tar extraction overwrites our copy. Both
paths need the rewrite; manual installs are a pure no-op (we
never wrote the cfg in the first place).

---

### Crash restart policy pushed to Node

`CrashPolicy`, `MaxCrashCount`, `CrashWindowMinutes` are fields on
`StartInstanceRequest` — the node enforces them autonomously so
restarts work even if the Manager is offline.

---

### Graceful instance shutdown

Current approach: `taskkill /PID <pid>` (no `/F`) via
`SendCtrlCToProcess`. Sends WM_CLOSE, works for most console apps.

**KNOWN LIMITATION**: UE4 dedicated servers (including MistServer) do
not respond to WM_CLOSE. No clean graceful shutdown available in .NET
8 for UE4 servers because:
- `ProcessStartInfo.CreateNewProcessGroup` was proposed in 2020 but
  never shipped in any .NET version
- Reflection on `_standardCreationFlags` fails because the field
  doesn't exist in .NET 8 (available private fields: _fileName,
  _arguments, _directory, _userName, _verb, _argumentList, _windowStyle,
  _environmentVariables, and k__BackingField variants)
- Native CreateProcess + CREATE_NEW_PROCESS_GROUP + manual pipes +
  reflection to attach streams was tried and broke stream redirection
- SendKeys to child window (WindowsGSM's approach) requires a visible
  window; removing `-log` prevents UE4 from creating one
- PostMessage WM_KEYDOWN Ctrl+C to all child windows: UE4 ignores

Force-kill via `Process.Kill(entireProcessTree:=True)` after 25s
timeout is the fallback. Parked pending Process Monitor investigation
of WindowsGSM's CreateProcess flags.

---

### Windows service deployment

`install-service.bat` (in GSM.Node output directory):
```
sc create GSMNode binPath= "<path>\GSM.Node.exe" start= auto
sc description GSMNode "PowerGSM Node Service"
sc start GSMNode
```

`uninstall-service.bat`:
```
sc stop GSMNode
sc delete GSMNode
```

Deploy via `dotnet publish -c Release --self-contained -r win-x64`
to get a standalone binary distribution.

---

### Tailer position cursor

The tailer in `ProcessManager.TailFileAsync` persists a per-(instance,
log file) cursor to the node's SQLite DB so log history isn't replayed
on instance restart. Without this, games that append to the same log
file across runs (Factorio's `factorio-current.log` and the
`--console-log` file) caused EventStore to re-process all prior chat /
join / leave events every time the instance started, producing
duplicate rows in `chat_messages` (visible in the Chat tab as the
same message at three different timestamps after three restarts).
Games that archive the old log and create a new one per run (LO via
UE4's timestamped log-archive convention) didn't hit the bug because
the tailer saw a fresh file — but they also didn't get clean
Manager-restart-while-instance-running behaviour, which the cursor
fixes too.

**Storage:** new `TailerPositions` table on the node DB, composite
PK on `(InstanceId, LogPath)`. Columns: `BytePosition` (Int64),
`Fingerprint` (TEXT), `UpdatedAtUtc` (TEXT). One row per file an
instance is tailing; Factorio gets two rows because it tails both
`factorio-current.log` and `factorio-console.log`. Created via
`CREATE TABLE IF NOT EXISTS` in `NodeDatabase.EnsureCreated` — no
migration mechanism on the node side, just additive schema on next
node start.

**Fingerprint:** SHA-256 hex of the file's first 256 bytes, computed
lazily on first open via `TryComputeFingerprint(fs)`. Returns Nothing
if the file is shorter than 256 bytes — files that small don't
justify the resume overhead, and locking in a hash of a partial file
would change as the file grew, defeating the comparison. When
fingerprint is Nothing the cursor isn't persisted on that iteration
and the next iteration retries.

**Resume logic on first open:**
- Compute current fingerprint (Nothing for sub-256-byte files)
- Read saved cursor for `(instanceId, logPath)`
- Resume only when (saved row exists) AND (current fingerprint
  computed) AND (fingerprints match) AND (fs.Length >= savedPosition).
  All four conditions required.
- Otherwise fall back to existing size-based heuristic

**File-replacement detection** (LO archive case):
- New file at same path → first 256 bytes differ → fingerprint
  mismatch → no resume → existing heuristic kicks in (read from 0
  for small files, backfill last 512KB for large ones)

**Truncation handling:** if `fs.Length < position` mid-run, both
`position` and `currentFingerprint` are reset. Next save iteration
recomputes fingerprint against the new (shorter) content.

**Persistence ordering — the StreamReader gotcha** (added to the VB
gotchas table): `Using reader As New StreamReader(fs)` disposes `fs`
on `End Using` because StreamReader closes the underlying stream by
default. Calling `TryComputeFingerprint(fs)` after the inner Using
block throws `ObjectDisposedException`. Fix: compute the fingerprint
BEFORE the StreamReader takes ownership, while `fs` is still alive,
then save afterward using the cached fingerprint without touching
`fs`. Caught during initial deployment — surfaced as a warn-level
"Tailer error" log + `System.IO.FileStream.get_Length()` stack frame.

**Persistence cadence:** save runs after every successful read
iteration (every ~500ms when content is flowing). SQLite writes are
sub-millisecond; the density determines how much progress is lost on
an unexpected node crash mid-tail (worst case: ~500ms of unread tail
that gets re-read on next start, harmless because the cursor catches
up immediately on the following save).

**First-deploy behaviour:** the very first instance restart after
deploying this still replays history once, because the table is
empty and there's no saved row to compare against. After that first
run completes, the cursor is in the DB; subsequent restarts are
clean.

**Pre-existing duplicate rows are not auto-cleaned.** Whatever
duplicates landed in `chat_messages` before the fix stay where they
are. Manual cleanup if desired:
```sql
DELETE FROM chat_messages WHERE instance_id = '<instance>';
```
against `<node-data-dir>/node.db` (and the equivalent EF query against
the manager DB's `ChatMessages` table). Going forward, no new
duplicates are created.

**Verification log line:** when resume succeeds, the tailer logs
`Resuming tailer for {Id} at byte {Pos} ({Path})` at Information
level. Absence of this line on the second restart after deploy
means either (a) the file replaced (fingerprint mismatch, expected
for LO) or (b) the saved position was past `fs.Length` (truncation).
No log spam in the steady-state save path — only the resume
decision is logged.

**Files modified:**
- `GSM.Node\NodeProgram.vb` — `TailerPositions` table in
  `EnsureCreated`, `GetTailerPosition` / `SaveTailerPosition` methods
  on `NodeDatabase`, new `TailerPositionRow` class
- `GSM.Node\ProcessManager.vb` — `TailFileAsync` consults saved
  cursor on first open, persists after every successful read,
  truncation path clears cached fingerprint; new
  `TryComputeFingerprint` static helper

---

### Phase 4c-1 — Node-side file operations

Foundation for everything else. New plugin interface declares which
directories on the install are user-manageable; new node endpoints
expose CRUD against those directories.

**New contracts in `GSM.Contracts\IGamePlugin.vb`:**

- `IManagedDirectoriesProvider` opt-in interface with one method:
  `GetManagedDirectories(config) As IReadOnlyList(Of ManagedDirectory)`.
- `ManagedDirectory` data class: `RelativePath`, `DisplayName`,
  `Permissions As DirPermissions`, `AllowedExtensions As List(Of String)`.
- `DirPermissions` enum (Flags): `Read | Write | Delete`.
- `RelativePath` may contain the literal token `{InstanceId}`,
  which the Manager substitutes before sending to the node —
  reserved for future multi-instance-per-installation games.

**New wire DTO in `GSM.Contracts\NodeApiContract.vb`:**

- `FileEntry` (RelativePath, SizeBytes, ModifiedUtc) — returned
  from list and upload endpoints.

**New node endpoints (all under `/api/instances/{id}/files`):**

- `GET ?path=...&allowedRoots=...&allowedExtensions=...` — list
- `GET /download?...` — streamed response body
- `POST /upload?...` — streamed request body, returns `FileEntry`
- `DELETE ?...` — single-file delete (idempotent, 404 → returns false rather than error)
- `POST /rename?...&newPath=...` — atomic rename
- `POST /copy?...&newPath=...` — file copy

Validation runs server-side on every request and rejects:

- Paths containing `..` segments (after normalisation)
- Paths whose resolved relative-to-install location isn't under
  one of the request's `allowedRoots` (equality OR `StartsWith`
  with directory separator)
- Paths whose extension isn't in `allowedExtensions` (when the
  list is non-empty)

The `allowedRoots` and `allowedExtensions` are sent by the Manager
on every call — the node doesn't store the plugin's declarations.
This keeps the security boundary explicit at the wire level: even
if the Manager is compromised it can't widen access beyond what
the plugin's declaration covers, because the Manager-side wrapper
derives both from the plugin's `ManagedDirectory` list.

**Streaming uploads.** The upload endpoint disables ASP.NET Core's
default form-options size limit and reads `Request.Body` directly
via `CopyToAsync` to a `FileStream`. No buffering. A 100MB Factorio
save uploads with constant memory on the node. Manager-side mirror:
`HttpClient.PostAsync(StreamContent(fileStream))` with a
one-shot `HttpClient` whose `Timeout = InfiniteTimeSpan` (manager
passes its own `CancellationToken` for cancel/abort).

**Manager-side wrappers** in `GSM.Manager\Core\NodeHttpClient.vb`
plus `INodeClient` interface members: `ListFilesAsync`,
`DownloadFileAsync` (caller-provided destination Stream),
`UploadFileAsync` (caller-provided source Stream),
`DeleteFileAsync`, `RenameFileAsync`, `CopyFileAsync`. Each
forwards `installPath`, `path`, `allowedRoots`, and
`allowedExtensions` verbatim to the node — the wrapper does
zero validation of its own.

---

### Node abuse prevention / hardened auth

**Threat model**: the node's listen port is typically internet-exposed
(directly or via port forwarding). Bearer-token auth alone is insufficient
against attackers who:
- brute-force the AuthToken via repeated requests
- mount timing attacks against `String.Equals`
- flood `/api/version` (intentionally unauthenticated for connectivity tests)
- DoS legitimate traffic via giant request bodies or connection floods

**Three-layer defense** lives in `GSM.Node\Security\SecurityServices.vb`:

1. **`RequestRateTracker`** — per-IP global request rate limit on a
   sliding 1-minute window. Default 600 req/min/IP. Catches
   `/api/version` flooding and post-auth API hammering. Returns 429
   with `Retry-After: 60`.

2. **`AuthFailureTracker`** — per-IP failed-auth lockout. Default
   thresholds: 10 failures in 5 minutes triggers a 15-minute lockout.
   Successful auth clears the IP's failure history (does NOT clear an
   active lockout — the existing penalty rides out). Returns 429 with
   `Retry-After: <seconds remaining>`.

3. **`SecurityHelpers.FixedTimeStringEquals`** — constant-time token
   comparison. Both inputs are SHA-256 hashed first so
   `CryptographicOperations.FixedTimeEquals` can compare fixed-length
   digests; original lengths are not leaked through the comparison.

Plus a 250ms delay on every failed auth (slows brute force well below
the lockout-detection threshold) and a generic `401 Unauthorized` body
with no distinction between missing header vs invalid token.

**Middleware order** (`AuthAndRateLimitMiddleware` in `NodeProgram.vb`),
cheapest rejection first:

1. Rate-limit check → 429
2. Lockout check → 429 with remaining-seconds Retry-After
3. `/api/version` skip → pass through unauthenticated
4. Bearer token check (constant-time)
   - on failure: record failure, sleep `AuthFailureDelayMs`, return
     generic 401
   - on success: reset IP failure history, call `nextDelegate`

The inline `app.Use(Async Function(...))` lambda was extracted to a
named `Private Async Function AuthAndRateLimitMiddleware(...)` and
wired with `app.Use(AddressOf AuthAndRateLimitMiddleware)` per the
VB.Net async-lambda guidance.

**Kestrel hardening** in `ConfigureKestrel`:

- `AddServerHeader = False` — don't broadcast Kestrel/version in
  responses
- `Limits.MaxRequestBodySize = 4MB` (down from 30MB default)
- `Limits.MaxConcurrentConnections = 100`
- `Limits.RequestHeadersTimeout = 30s`

**Configuration** — new `Security` section in `nodesettings.json`:

```json
"Security": {
    "MaxFailedAttempts": 10,
    "FailureWindowMinutes": 5,
    "LockoutMinutes": 15,
    "AuthFailureDelayMs": 250,
    "RequestsPerMinutePerIp": 600,
    "MaxRequestBodyBytes": 4194304,
    "MaxConcurrentConnections": 100
}
```

Bound to a `SecurityConfiguration` POCO via
`builder.Configuration.GetSection("Security").Bind(secConfig)` and
registered as a singleton alongside the trackers.

**Operational notes**:

- All tracker state is **in-memory only**. Node restart clears all
  lockouts and rate counters — use a restart as the emergency reset
  if a legitimate operator's IP gets locked out (e.g. from an
  AuthToken mismatch during testing).
- IP source is `context.Connection.RemoteIpAddress` — the direct TCP
  peer. If the node is later placed behind a reverse proxy (nginx,
  Cloudflare, IIS ARR), all traffic appears to come from one IP and
  legitimate Manager traffic gets locked out alongside attackers. In
  that scenario, add `app.UseForwardedHeaders` and configure
  `KnownProxies`/`KnownNetworks`.
- The `/api/auth` endpoint also uses `FixedTimeStringEquals` (hygiene),
  but is reachable only after the middleware has already validated
  the Bearer token, so it can't be brute-forced from outside.
- Both trackers `Implements IDisposable` and own a 5-minute
  `Timer`-based cleanup that prunes stale entries to keep memory
  bounded under churn (botnet scans, transient client IPs).
- No new NuGet packages — uses `System.Security.Cryptography` and
  `System.Collections.Concurrent`, both built-in to .NET 8.

**Files added**:

- `GSM.Node\Security\SecurityServices.vb` — `SecurityConfiguration`,
  `SecurityHelpers`, `AuthFailureTracker`, `RequestRateTracker`

**Files modified**:

- `GSM.Node\NodeProgram.vb` — security service registration, Kestrel
  hardening, named `AuthAndRateLimitMiddleware` replacing the inline
  auth lambda
- `GSM.Node\nodesettings.json` — new `Security` section
- `GSM.Node\Endpoints\NodeEndpoints.vb` — `Imports GSM.Node.Security`;
  `/api/auth` uses `FixedTimeStringEquals` instead of `String.Equals`

**Sanity-check from another machine** (with a 36-char base64 token):

- `curl -i http://node:8765/api/status` → `401`, body `{"error":"Unauthorized"}`
- 11x bad-token loop → 10th gets 401, 11th onward gets 429 with
  `Retry-After: 900`
- `curl -i http://node:8765/api/version` → still 200 (intentionally public)
- 601 rapid-fire calls to anything from one IP → 429 from the rate
  tracker on the 601st

---

## PHASE 7 — GSM.NodeSetup (companion configuration tool)

Goal: a setup companion that ships next to `GSM.Node.exe` so end users
do not have to hand-edit `nodesettings.json`. Two UIs: a WinForms GUI on
Windows and an interactive console UI on Linux. Both surfaces share one
config schema, one validator set, and one service-installer module.

**This is a standalone project.** No project references, no shared
assemblies, no plugin contracts. The setup tool reads the same JSON the
node reads, but is otherwise independent.

### Project layout

```
GSM.NodeSetup/
├── GSM.NodeSetup.vbproj      multi-targeted: net8.0;net8.0-windows
├── Program.vb                entry point + arg parsing + GUI/CLI dispatch
├── NodeSetupConfig.vb        POCOs mirroring nodesettings.json + load/save
├── ConfigHelpers.vb          token gen, validators, elevation detection
├── ServiceManager.vb         sc.exe (Windows) + systemd unit gen (Linux)
├── ConsoleUi.vb              interactive menu + 5-step wizard (cross-platform)
└── Windows/                  excluded from net8.0 build via Compile Remove
    ├── GuiBootstrap.vb       ApplicationConfiguration.Initialize + Application.Run
    └── MainSetupForm.vb      single TabControl form, all in code
```

### Multi-targeting strategy

```xml
<TargetFrameworks>net8.0;net8.0-windows</TargetFrameworks>

<PropertyGroup Condition="'$(TargetFramework)'=='net8.0-windows'">
  <UseWindowsForms>true</UseWindowsForms>
  <DefineConstants>$(DefineConstants),WINDOWS_GUI=True</DefineConstants>
</PropertyGroup>

<ItemGroup Condition="'$(TargetFramework)'!='net8.0-windows'">
  <Compile Remove="Windows\**\*.vb" />
  <None Include="Windows\**\*.vb" />
</ItemGroup>
```

- Linux build (`net8.0`) compiles only the cross-platform files. The
  `Windows\` folder is removed from the Compile item group entirely.
  No WinForms type ever enters the Linux build, so it runs on a
  bare .NET 8 runtime with no GUI dependencies.
- Windows build (`net8.0-windows`) compiles everything. The
  `WINDOWS_GUI=True` constant is defined for that TFM only, so
  `Program.vb` can `#If WINDOWS_GUI Then ... #End If`-guard the call
  into `Windows.GuiBootstrap.Run`.
- The VB.Net `<DefineConstants>` syntax for boolean constants is
  `NAME=True` joined with commas. Plain `WINDOWS_GUI` (no `=True`)
  also works in newer SDKs, but the `=True` form is unambiguous and
  matches Microsoft's own documentation.

### Mode selection (Program.vb)

| Argument | Behaviour |
|---|---|
| (none) on Windows | GUI |
| (none) on Linux | Console wizard |
| `--cli` / `-c` | Force console |
| `--gui` | Force GUI (errors on Linux build) |
| `--auto-init` | Non-interactive: write fresh config, generate token, print to stdout, exit. For Docker / cloud-init / Ansible. |
| `--config <path>` | Override path to `nodesettings.json`. Default is `AppContext.BaseDirectory\nodesettings.json`. |
| `--help` / `-h` | Usage |

If the GUI bootstrap throws, the tool falls back to console mode
instead of failing — useful when a user double-clicks the exe on a
Windows Server Core install with no graphical session.

### Config round-trip (NodeSetupConfig.vb)

- POCOs mirror the exact shape of `nodesettings.json`: `Node`,
  `Security`, `Logging` sections.
- `LoadOrCreate(path)` returns a populated default when the file is
  missing or empty (first-run state). Throws on a malformed file —
  silent corruption recovery would mask the user's misconfiguration.
- `Save(path, backupExisting)` does an atomic write: write to
  `<path>.tmp`, then `File.Move` with `overwrite:=True` to the real
  path. Optionally copies the existing file to `<path>.bak` first
  (best-effort — backup failure does not block the save).
- `JsonSerializerOptions` uses `PropertyNameCaseInsensitive = True`
  on read (so users editing the JSON by hand can be sloppy with case)
  and `WriteIndented = True` on write (so the saved file stays
  human-editable).
- `NeedsAuthTokenSetup` returns True when the token is missing or
  still the literal `CHANGE_ME_BEFORE_FIRST_RUN` placeholder. Both UIs
  surface this as the "NOT CONFIGURED" status indicator.

### Token generation (ConfigHelpers.vb)

- 36 random bytes from `RandomNumberGenerator.Create()` →
  `Convert.ToBase64String` produces a 48-char token. Matches the
  `openssl rand -base64 36` suggestion from the original reference doc.
- `IsAuthTokenPlaceholder` is case-insensitive on the literal value
  to catch users who typed lowercase.
- `RunningElevated()` returns True when the process can perform
  privileged service ops: `WindowsPrincipal.IsInRole(Administrator)`
  on Windows, `Environment.UserName == "root"` on Linux. Used to
  short-circuit service-install attempts that would otherwise fail
  with `[SC] OpenSCManager FAILED 5` or systemctl permission errors.

### Service installation (ServiceManager.vb)

**Windows path** — shells out to `sc.exe`:
- `sc create <name> binPath= "<exe>" DisplayName= "<name>" start= auto`
- The space after `binPath=` and `start=` is **required**; without it,
  sc.exe creates a service that never starts. (sc.exe parses `KEY=`
  as a separate token from the value.)
- `sc description` is best-effort (failure is non-fatal).
- `sc start` runs after create; if it fails the install is still
  reported as successful with a hint to start manually.
- All `sc` invocations use `ProcessStartInfo.ArgumentList` (NOT
  `Arguments`) so each argument is escaped independently and we avoid
  the same trailing-backslash + escaped-quote root-cause that bit
  SteamCMD.
- `GetWindowsServiceStatus` parses the `STATE` line from `sc query`,
  returning `Running` / `Stopped` / `Starting` / `Stopping` /
  `NotInstalled` / `Unknown`. NotInstalled is detected by the `1060`
  error code in the output.

**Linux path** — generates a `gsmnode.service` systemd unit:
- `Type=simple`, `Restart=on-failure`, `RestartSec=5`,
  `StandardOutput=journal`, `StandardError=journal`.
- `User=` line is included only when the user supplied a value (via
  GUI textbox or CLI prompt). Empty user → admin must edit the unit
  before installing.
- The unit is written to `<output-dir>/gsmnode.service`. The tool
  does NOT shell out to `systemctl` itself — it prints the three-line
  copy/enable/start instruction block for the admin to run as root.
  Reasons: systemctl needs root, sudo non-interactive cannot be
  assumed, distro-specific paths vary, and container-only
  environments may not have systemd at all.
- `GetSystemdStatus` shells out to `systemctl is-active gsmnode` for
  status display. Returns `Running` / `Stopped` / `Failed` /
  `Starting` / `Stopping` / `NotInstalled` / `Unknown`.

### Console UI (ConsoleUi.vb)

- First-run heuristic: if the file does not exist OR the auth token
  is the placeholder, the tool launches straight into the wizard.
  Otherwise it shows the main menu.
- Five-step wizard: identity, storage, operations, authentication,
  review. Defaults shown in `[brackets]`; Enter accepts the default.
- Validator helpers return `Nothing` for valid input, an error
  string for invalid input, or a `"Warning: ..."` / `"Note: ..."`
  prefixed string for non-fatal issues. The prompt loop accepts
  warnings and re-prompts on errors.
- After save, the new auth token is displayed prominently in a
  highlighted block so the user can copy it into the Manager.
- Color via `Console.ForegroundColor`. Honors the `NO_COLOR` env
  var convention and disables color when `Console.IsOutputRedirected`
  is True (don't dump ANSI codes into log files).
- Edit submenu lets users tweak individual fields. Security settings
  are behind a sub-submenu with a banner warning that defaults are
  fine for most setups and a Reset-to-defaults shortcut.

### WinForms GUI (Windows\MainSetupForm.vb)

- Single Form with TabControl: General, Auth Token, Security, Service.
- Built entirely in code — no `.resx` designer file (matches
  `MainForm.vb` in GSM.Manager).
- `TableLayoutPanel` for two-column field grids (label | control)
  with absolute-width label column and percent-fill control column.
- Auth Token tab uses `UseSystemPasswordChar` masking by default
  with a "Show token" checkbox to unmask. "Copy" button writes to
  `Clipboard.SetText` with a 1.2-second "Copied!" feedback flash via
  a one-shot `Timer` (which then disposes itself in its `Tick`
  handler — clean enough for a transient effect).
- Service tab buttons short-circuit with friendly MessageBox dialogs
  when not elevated, instead of letting `sc.exe` print
  `Access is denied`.
- Bottom action bar uses `FlowDirection.RightToLeft` so the buttons
  read Cancel / Save and Exit / Save / Generate Token from right to
  left as added — matches the Windows convention.
- `AcceptButton = SaveButton`, `CancelButton = CancelButton`.

### Files added in this phase

- `GSM.NodeSetup\GSM.NodeSetup.vbproj` — multi-targeted project
- `GSM.NodeSetup\Program.vb` — entry point
- `GSM.NodeSetup\NodeSetupConfig.vb` — config schema + JSON I/O
- `GSM.NodeSetup\ConfigHelpers.vb` — pure functions used by both UIs
- `GSM.NodeSetup\ServiceManager.vb` — service install/uninstall/status
- `GSM.NodeSetup\ConsoleUi.vb` — interactive console UI
- `GSM.NodeSetup\Windows\GuiBootstrap.vb` — WinForms init helper
- `GSM.NodeSetup\Windows\MainSetupForm.vb` — main GUI form
- `GSM.Node\install-service.bat` — fallback for headless deployments
  (was referenced by the vbproj but missing from disk)
- `GSM.Node\uninstall-service.bat` — fallback for headless deployments

### Files modified in this phase

- `PowerGSM.sln` — added GSM.NodeSetup project entry with new
  project GUID `{A1B2C3D4-4444-4444-4444-000000000004}` and
  Debug/Release configuration mappings.

### Build / publish notes

- Build the whole solution as before. The new project produces TWO
  output folders under `bin\Debug\` and `bin\Release\`:
  `net8.0\` (cross-platform) and `net8.0-windows\` (Windows GUI).
- For Windows distribution, publish the `net8.0-windows` TFM:
  `dotnet publish GSM.NodeSetup -c Release -f net8.0-windows -r win-x64 --self-contained`
- For Linux distribution, publish the `net8.0` TFM:
  `dotnet publish GSM.NodeSetup -c Release -f net8.0 -r linux-x64 --self-contained`
- The setup binary belongs next to `GSM.Node.exe` / `GSM.Node` in
  the deployment package — the tool resolves both `nodesettings.json`
  and `GSM.Node.exe` via `AppContext.BaseDirectory`.

---

## PHASE 5g-1 follow-up — Cross-platform hardening (May 2026)

After Phase 5g-1 shipped, real-world testing against a Linux
node running Last Oasis surfaced a series of platform-specific
and stability bugs that don't fit any single existing phase but
represent durable architectural knowledge. They're collected
here as one phase-follow-up section because they share a
common theme — cross-platform hardening of the spawn / tail /
stream / persist pipeline.

### Linux UE4 file tailing via libc.open

UE4 dedicated servers on Linux hold an advisory `flock(LOCK_EX)`
on their `.log` file (lsof shows `MistServ ... 3uW` — fd 3,
mode u r+w, capital W = write lock on the entire file). .NET
8's `FileStream` consults the advisory lock and refuses to
open even with `FileShare.ReadWrite Or FileShare.Delete`. The
node bypasses this via P/Invoke:

```vb
<DllImport("libc", EntryPoint:="open", SetLastError:=True)>
Private Shared Function LibcOpen(path As String, flags As Integer) As Integer
End Function
```

`ProcessManager.OpenLogFileForTailing(path)` calls `LibcOpen`
with `O_RDONLY` (= 0), wraps the returned fd in
`SafeFileHandle(handle, ownsHandle:=True)`, and constructs
`New FileStream(handle, FileAccess.Read)`. Windows falls
through to the normal FileStream ctor since flock semantics
don't apply. Subsequent reads go through normal stream APIs;
only the open path differs.

### Cross-platform spawn-strategy / file-tailer duality

Linux forces `SpawnStrategy.StdoutCapture` (Strategy A) for
all games via `ResolveStrategy`. The implication is non-
obvious: on a fresh spawn under Strategy A, stdout is the
authoritative log source and the file tailer MUST NOT also
run (produces per-line duplicates). On adoption under
Strategy A, stdout was owned by the previous node process
and is no longer connected — file tailer is the only
option. The asymmetry is gated in `FinalizeStart`:

```vb
If managed.Strategy <> SpawnStrategy.StdoutCapture Then
    StartFileTailers(managed, managed.LogFilePaths)
End If
```

`TryAdoptOne` (adoption path) unconditionally starts the
tailer. Windows Strategy B (HiddenConsoleDirect) and Linux
Factorio Strategy C (NativeTerminal) don't capture stdout for
the log buffer regardless and don't trigger the gate.

### Chat dedup via UE4 timestamp + UNIQUE INDEX

Node-side `EventStore.ProcessLine` extracts the
`[YYYY.MM.DD-HH.MM.SS:fff]` UE4 prefix via
`TryParseUe4Timestamp` and uses the parsed value as the
persisted `timestamp_utc` rather than `DateTime.UtcNow`
from `EmitTailLine`. Lines without a parseable UE4
timestamp (Factorio, plain text) fall back to `UtcNow`.
`chat_messages` has a `ux_chat_dedup` UNIQUE INDEX on
`(instance_id, timestamp_utc, display_name, text)`;
persistence uses `INSERT OR IGNORE`. Makes adoption replay
(`skipResume:=True`) idempotent — the entire ring buffer's
chat lines re-flow through ProcessLine on every adoption,
but each row collides with the already-persisted row by
source timestamp and gets silently dropped.

UE4 timestamp regex note: the seconds-to-millis separator
is `:` not `.` (e.g. `[12.34.56:789]`), which requires a
capture-group split — `DateTime.ParseExact` with format
`yyyy.MM.dd-HH.mm.ss:fff` doesn't work because `:` is
parsed as a time-component separator. Regex captures the
date-seconds half and the millis half separately, then
concatenates with `.` for the final `DateTime.Parse`.

### Atomic SSE subscription (SubscribeAndGetTail)

When a manager subscribes to a node SSE log stream, both
the backfill ("give me the last N lines") and the live-
stream start point ("deliver everything from here on") must
come from one consistent `_writePos` snapshot. Old code
took the buffer's SyncLock twice in sequence — once via
`AddSubscription` setting `LastSequence = _writePos - 1`,
once via `GetTail` — and an `Append` firing between the
two acquisitions placed the new line in BOTH halves.
`InstanceBuffer.SubscribeAndGetTail(subscription, tailCount)`
does both under a single lock: tail returns
`(_writePos - take)..(_writePos - 1)`, live stream starts
at `_writePos`. No overlap, no gap. Legacy two-call entry
points remain with deprecation comments for callers that
don't need both halves.

---

### Linux signal isolation via setsid

The kernel routes SIGINT (Ctrl+C) to every process in the
controlling terminal's process group. Game children
spawned by the node default to the same process group, so
Ctrl+C delivered to the node's terminal would also kill
every running game-server child. `ProcessManager.WrapInSetsidIfLinux(psi)`
rewrites `ProcessStartInfo` so children spawn as
`setsid <exe> <args>`, detaching them into a new session
and process group. The node's own Ctrl+C handler still
signals game children explicitly via gsm-broker when an
instance stop is requested; the only thing setsid blocks
is incidental terminal propagation. Idempotent — re-
wrapping a setsid-wrapped psi is detected (FileName ==
setsid + first arg already the original exe) and is a no-op.

---

### UE4 stdout vs stderr mental model

UE4 dedicated servers emit log lines to stdout exclusively.
Stderr carries SteamAPI initialisation noise only
(`[S_API] SteamAPI_Init()`,
`RecordSteamInterfaceCreation (PID N)`) — not a mirror of
stdout. Confirmed empirically via diff test on Linux LO:
2613 stdout lines vs 15 stderr lines during one start
cycle. Node drops stderr in `AttachProcessHandlers` (still
drains the pipe to prevent the kernel buffer from filling
and blocking writes, just doesn't append to the ring
buffer). When investigating a log-doubling symptom, stderr
is NOT the suspect; look elsewhere (SSE subscription
lifecycle, file tailer gating, ring-buffer atomicity).

---

### Host-side prerequisite checks (Phase 5g side-feature)

Lets a plugin declare host-side runtime dependencies its
game needs to launch, so missing prereqs surface as
pre-install notices BEFORE the user spends 15–30 minutes
downloading a depot only to watch the process exit silently
at launch. Motivating case was Conan Exiles — ships no
`_CommonRedist` folder, links against Microsoft VC++ 2015–2022
x64 at runtime, fails with `STATUS_DLL_NOT_FOUND`
(-1073741515) and no log file when the runtime is missing.

**Architecture: "Manager declares, Node detects, Manager
renders."** Plugin names opaque prereq strings via the new
`IPrerequisiteProvider` interface; Manager passes the list
to the node's `/api/system/prerequisites` endpoint; node
owns the catalog of recognised names + detection logic +
display metadata and returns enriched results. Manager
renders the response as Warning-severity notices in
NewInstallationForm. No catalog duplication across the
boundary — adding a new prereq is a node-side change that
requires no plugin-contracts version bump (older nodes
return `Recognized=False` for unknown names and the
Manager silently skips them).

Catalog as of 0.5.0: `vcredist-2015-2022-x64` (registry
probe), `linux-xvfb` and `linux-unzip` (PATH-walk probes via
`ProbeLinuxBinary`; `python3` also satisfies linux-unzip; on
non-Linux nodes both report Installed=True — "satisfied /
not applicable" — because `GetRequiredPrerequisites` takes no
platform parameter, so plugins declare Linux prereqs
unconditionally).

**Plugin contract (`GSM.Contracts/IGamePlugin.vb`).**
New optional interface alongside `IInstallationNoticeProvider`:

```vb
Public Interface IPrerequisiteProvider
    Function GetRequiredPrerequisites() As IReadOnlyList(Of String)
End Interface
```

Names are lowercase kebab-case, version-suffixed when the
runtime has multiple incompatible major versions. Initial
catalog entry: `vcredist-2015-2022-x64`. Plugins that don't
implement the interface skip the prereq check entirely.

**Wire DTOs (`GSM.Contracts/NodeApiContract.vb`).**
`PrerequisiteCheckResult` carries: `Name`, `Recognized`,
`Installed`, `Version`, `DisplayName`, `DownloadUrl`,
`Instructions`. `PrerequisiteCheckResponse` wraps a
`List(Of PrerequisiteCheckResult)` parallel to the request's
names list. `INodeClient.CheckPrerequisitesAsync(names,
cancellation)` is the typed wrapper.

**Node side (`GSM.Node/PrerequisiteProbe.vb` + endpoint in
`NodeEndpoints.vb`).** Static `Private Shared ReadOnly
_catalog As Dictionary(Of String, CatalogEntry)` keyed
case-insensitively on the name; each entry carries
DisplayName + DownloadUrl + Instructions. `CheckSingle`
dispatches by `name.ToLowerInvariant()` to a per-prereq
probe helper. Adding a new prereq is two edits: an entry
in `_catalog` AND a matching `Case` clause in `CheckSingle`
pointing at a new probe helper.

Probe for `vcredist-2015-2022-x64` reads
`HKLM\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64`
under `RegistryView.Registry64` and checks `Installed = 1`
(DWORD). This is Microsoft's own canonical "is it installed"
key — the 14.x ABI is shared by VC++ 2015, 2017, 2019, 2022,
so the single probe covers all four marketing names. Also
reads `Version` (e.g. `"14.38.33135.0"`) for diagnostics;
the notice fires off `Installed` alone but the version
round-trips through the response for future use.

Registry calls are guarded by `OperatingSystem.IsWindows()`
so a Linux node querying the same prereq returns
`Installed=False, Version=""` without throwing. Registry
permission failures (or missing-hive on non-standard
systems) are swallowed and treated as "not installed" —
false positives on the missing side are vastly preferable
to false negatives (which would let the user proceed into
a silent-crash install).

Endpoint: `GET /api/system/prerequisites?names=a,b,c`.
Comma-separated single query value rather than repeated
`?names=a&names=b` — simpler on both sides for the small
finite list, and individual names are escaped via
`Uri.EscapeDataString` on the Manager side so embedded
commas survive. Authenticated by the standard middleware
(anything under `/api/` that isn't `/api/version` or
`/api/auth`).

**Manager side (`NewInstallationForm.RefreshNoticesAsync`).**
Replaces the previous sync notice-fetch in `OnGameChanged`
with a two-phase async pattern, also hooked from
`OnNodeChanged`:

  1. **Phase 1 (sync, immediate)**: render notices from
     `IInstallationNoticeProvider.GetPreInstallNotices()`.
     User sees visual feedback for the game change without
     waiting on the wire call.
  2. **Phase 2 (async, wire call)**: if the plugin
     implements `IPrerequisiteProvider` AND a node is
     selected, call `CheckPrerequisitesAsync` on the node.
     For each result that's `Recognized=True AndAlso
     Installed=False`, synthesise a Warning notice using
     the node-supplied display fields. Re-render combined
     (static first, then dynamic).

If nothing's missing (or the plugin doesn't implement
the interface, or no node is selected), the Phase-1 render
is the final state — no re-render needed.

Cancellation: each `RefreshNoticesAsync` call cancels and
replaces a class-level `_prereqCheckCts` so fast game- or
node-switching doesn't let a stale wire response clobber
notices for the current selection. Resumes on the captured
UI SyncContext so the final `RebuildNoticesPanel` call
lands on the UI thread without an explicit marshal.

All failure modes are silent — prereq notices are
quality-of-life, never gating:
- `OperationCanceledException` from CTS replacement: bail
- `NodeApiException` with `StatusCode = NotFound` (pre-
  prereq-feature node, older binary, endpoint doesn't
  exist): bail with static notices left in place
- Generic catch (connection timeout, HTTP-level failure,
  deserialisation): bail with static notices left in
  place

The Phase-1 static notices always render even when Phase 2
fails, so a network hiccup at form-open doesn't strip the
user of game-level context they should see regardless of
prereq state.

**Notice rendering.** Body combines the node-supplied
`Instructions` text with a separate `Download: <url>` line
separated by a blank line, so the URL is visually distinct
from prose. Today the notice renderer doesn't make URLs
clickable (user copies the link) but the separation makes
that trivial.

**Conan plugin opt-in (`ConanExilesPlugin.vb`).** Adds
`Implements IPrerequisiteProvider` and a one-line
`GetRequiredPrerequisites` returning
`{"vcredist-2015-2022-x64"}`. The static
`IInstallationNoticeProvider` notices stay (Windows-only
advisory, Enhanced-vs-Legacy build context); the new
prereq notice is purely additive when the runtime is
missing.

**Why not bake VC++ install into the SteamCMD pipeline
(`_CommonRedist` synthesis)?** Considered and rejected:
Conan ships no `_CommonRedist` folder by design, and the
existing node-side scanner only runs after SteamCMD
completes. Synthesising a fake folder pre-SteamCMD risks
Steam's verify-integrity sweep deleting unrecognised
files. Bundling Microsoft's installer with the manager
adds ~25 MB per platform plus licensing concerns; downloading
on-demand from `aka.ms` is a per-install cost that's better
amortised as a one-shot manual install. Detection-only
notice was chosen as v1; auto-install ("Install for me"
button on the notice that downloads + runs silently) is
deferred to a future mini-phase since it adds non-trivial
moving parts: download progress UI, signature
verification, admin-elevation handling, retry-on-failure.

**Self-suggesting install path on form open
(`NewInstallationForm.OnShown` override).** Bundled in the
same work as the prereq feature: the form's default install
path suggestion (fetched from
`NodeStatusResponse.ServersDirectory` + the selected
plugin's `GameId`) wasn't populating at form open — only
appeared after the user changed game or node. Root cause
was that `RefreshSuggestedInstallPathAsync` used `Me.Invoke`
to read the combo selections from its background thread,
and the form's window handle doesn't exist when the
constructor's `Task.Run` fires it. `Me.Invoke` threw
`InvalidOperationException`; the outer `Try/Catch`
swallowed it; the path stayed empty. Subsequent event-
driven refreshes (Node Windows↔Linux switch, plugin
change) succeeded because by then the form was shown and
the handle existed. Fix: removed the constructor's
`Task.Run` and added a `Protected Overrides Sub OnShown(e)`
that fires `RefreshSuggestedInstallPathAsync()` directly
(UI thread, no `Task.Run` wrapper needed — the first `Await`
in the chain yields the UI thread back to the message pump
naturally). See also the matching VB.NET gotchas-table
entry for the underlying pattern.

**Future catalog growth.** The architecture admits arbitrary
runtime-dependency types (DirectX, .NET Desktop Runtime
versions, OpenAL, OS-bundled prerequisites for non-Windows
nodes when cross-platform plugins land) without contract
changes — only the node's catalog + a matching probe helper
per entry. Plugin names are opaque strings; older Manager
builds talking to a newer node still work because the
response shape is forward-compatible.

---

## Node self-update (Phase 8-2)

**A running process cannot replace the binary it is executing** (Windows locks
the live `.exe`; Linux keeps running the old inode after an unlink-replace). So
the node never swaps its own live files. It **stages** the new binary beside
itself and **exits**; a process that *outlives* the node does the swap in the
gap, then relaunches. Running games ride through because 8-1's shims keep them
alive across the node bounce and the relaunched node re-adopts them. **Status:
verified end to end on all four survivor paths — Linux (systemd + bare) and
Windows (service + bare); game PID unchanged across the bounce.** The
Manager-driven push lives in slice 7a (`manager.md`); the commit-time OS guard
and the health-gate + auto-rollback are slice 8 (below). Release-feed sourcing
and shim / NodeSetup co-update are the remaining slice-7 work.

### Staging endpoints (`SelfUpdate.vb` → `SelfUpdateService`, mapped in `SystemEndpoints`)

A chunk session, all bearer-gated under `/api/system/staged-binary/`:

- `POST .../begin` — JSON `{targetName?, totalBytes, sha256, version?}` →
  `{uploadId}`. Opens a temp `.part` beside the live binary; supersedes any
  in-flight session for the same target. `targetName` defaults to `"node"` and
  resolves to a **target shape** (7b/7c) — `node` / `nodesetup` / `shim`
  (unknown → 400). A `shim` begin also requires a path-safe `version` (it names
  the `GSM.Shim\<version>\` install folder); missing/unsafe → 400.
- `POST .../{uploadId}/chunk?offset=N` — raw body, appended **append-only**
  (`offset` must equal the current `.part` length, so a failed push resumes from
  the last good offset; mismatch → 409 with the expected offset). Streams
  straight to disk, bounded against `totalBytes` (overshoot → 413). The endpoint
  lifts its own request-body cap (`IHttpMaxRequestBodySizeFeature`) so a large
  chunk isn't clipped by the global Kestrel limit.
- `POST .../{uploadId}/commit` — verifies SHA-256 + declared size over the whole
  `.part`, runs the 8a OS-match guard, then **places** the bytes by shape: swap
  shapes (node / nodesetup) atomic-rename `.part` → `<binary>.new` (`File.Move(…,
  overwrite:=True)`); the shim's versioned install lands `.part` directly at
  `GSM.Shim\<version>\GSM.Shim[.exe]` **lock-safely** (`PlaceLockSafe`: delete if
  idle, else rename the live exe aside `*.superseded-*`, else **409** so a
  running-shim pin fails clean instead of tearing). `+x` on Linux. SHA/size
  mismatch → 422 and the `.part` is deleted.

Naming rule (must match the survivor): the live binary filename with `.new` /
`.old` / `.<uploadId>.part` appended, in `AppContext.BaseDirectory` — so
`GSM.Node.new` on Linux, `GSM.Node.exe.new` on Windows. The node only verifies
the bytes the Manager declares; the trust boundary is the Manager's verified
push (slice 7), the commit re-verify is integrity insurance.

### Target shapes (Phase 8-2 slices 7b/7c)

`ResolveTarget(targetName, version)` maps a target to a **shape**, and
`apply-update` dispatches on it (`ApplyResult.RequiresExit` tells the endpoint
whether to stop the host):

- **node — SwapWithSurvivor.** Stage `.new`; on apply the node update-exits and
  a survivor swaps `.new` over live and relaunches (below). `RequiresExit=True`.
- **nodesetup — SwapInPlace.** Stage `.new`; on apply the node swaps its **idle**
  `GSM.NodeSetup[.exe]` in place (`ApplyInPlaceSwap`: delete old `.old`, live →
  `.old`, `.new` → live, `+x`), staying up. `RequiresExit=False`. **No
  auto-revert** — a bad NodeSetup only bites on the *next* node apply; `.old` is
  kept for manual restore.
- **shim — VersionedInstall.** No `.new`, no apply-time work: `commit` already
  installed `GSM.Shim\<version>\GSM.Shim[.exe]`. Apply is a no-op
  (`RequiresExit=False`). Startup sweeps stray `GSM.Shim\**\*.part` /
  `*.superseded-*`.

### Update-exit + survivor selection (node target)

`POST /api/system/apply-update?target=node` — 409 if no `.new` is staged;
otherwise picks a survivor, schedules a graceful stop (after the 202 flushes),
and returns `{accepted, survivor}`. The stop is the **normal graceful path** —
`IHostApplicationLifetime.StopApplication()` → `ApplicationStopping` →
`DetachShimsForShutdown` (so the shim-backed games survive) — never a hard
`Environment.Exit`.

The survivor is chosen from `SystemdHelpers.IsSystemdService()`:

- **Under systemd** — defer to systemd. The node exits **non-zero (code 10)** so
  `Restart=on-failure` relaunches it, and the unit's idempotent `ExecStartPre`
  swaps `.new` into place first. (`IsWindowsService()` isn't consulted — Windows
  always takes the NodeSetup path below, service or bare.)
- **Everything else** (Windows service, Windows bare, **Linux bare**) — the node
  spawns a detached `GSM.NodeSetup --apply-update --wait-pid <self>` and exits
  **clean (0)**, because NodeSetup owns the relaunch and a non-zero exit from a
  Windows service would race SCM recovery. This is the **universal fallback
  survivor** that closes the gap where a Linux node run as a plain foreground
  exe (no systemd) would otherwise stage an update with no one to apply it.
  Windows spawns via native `CreateProcessW` with
  `CREATE_BREAKAWAY_FROM_JOB | DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP`
  (fallback `Process.Start` if breakaway is refused) so the SCM/job can't reap
  the survivor; Linux-bare uses a plain detached `Process.Start` (reparents to
  init when the node exits).

The exit-code rule is one predicate: **non-zero iff relying on systemd's
`Restart`; otherwise 0.** The flag is read from a `SelfUpdateService` reference
captured **before `app.Run()`** — `Run` disposes the host's DI container on
return, so resolving the service afterwards throws `ObjectDisposedException`,
which silently dropped the node back to exit 0 (it staged + set the flag but
never restarted under systemd). See the matching gotchas-table row.

### systemd `ExecStartPre` apply-or-revert (`ServiceManager.BuildSystemdUnit`)

The generated unit carries an idempotent step that runs before every
`ExecStart`. Slice 6 shipped the forward swap; slice 8b-2 made it apply-**or**-
revert and switched the unit to `Type=notify` (rendered with real paths):

```
ExecStartPre=/bin/sh -c 'if [ -f "<dir>/GSM.Node.new" ]; then mv -f "<dir>/GSM.Node" "<dir>/GSM.Node.old"; mv -f "<dir>/GSM.Node.new" "<dir>/GSM.Node"; chmod +x "<dir>/GSM.Node"; touch "<dir>/GSM.Node.update-pending"; elif [ -f "<dir>/GSM.Node.update-pending" ] && [ -f "<dir>/GSM.Node.old" ]; then mv -f "<dir>/GSM.Node" "<dir>/GSM.Node.failed"; mv -f "<dir>/GSM.Node.old" "<dir>/GSM.Node"; chmod +x "<dir>/GSM.Node"; rm -f "<dir>/GSM.Node.update-pending"; fi'
```

Forward (slice 6): the `[ -f .new ]` guard makes the apply a no-op on a normal
start, so **the presence of `GSM.Node.new` is the entire forward-update state**
— a staged-but-unapplied update (crash, reboot, power loss) is applied on the
next start. Revert (slice 8b-2): applying a `.new` also drops a `.update-pending`
marker that the node deletes once healthy (below), so a marker that *outlives* a
start means the update never came up — the `elif` then quarantines the bad
binary as `.failed`, restores `.old`, and clears the marker. `mv` within one
filesystem is atomic, no `set -e`, and each branch ends on an exit-0 command, so
an interruption self-heals next start and `ExecStartPre` never blocks
`ExecStart`. All renames stay within the powergsm-owned node dir (no extra
privilege). The unit also gains `Type=notify` + `NotifyAccess=main` (so a node
that starts but never signals ready counts as a *failed* start — `UseSystemd()`
already sends `READY=1`) and `StartLimitIntervalSec=200` / `StartLimitBurst=5`
to bound the restart loop. **Applying any of this to an existing systemd node
requires regenerating + reinstalling the unit once** so the new lines are
present.

### NodeSetup survivor (`GSM.NodeSetup\SelfUpdateApply.vb`)

`GSM.NodeSetup --apply-update --wait-pid <pid>` runs headless (dispatched in
`Program.vb` before the GUI/console wizard): wait for the node PID to die
(`GetProcessById(pid).WaitForExit`; "already gone" = success) → swap `.new` over
the live binary keeping `.old` (retried on a transient sharing violation /
handle-linger) → relaunch via `ServiceManager.StartWindowsService` (`sc start`,
treating sc-error 1056 "already running" as success) when the GSMNode service is
installed, else a direct exec of the swapped-in binary. Linux-bare always takes
the direct-exec leaf (Linux-under-systemd never reaches NodeSetup). Progress is
written to stderr and `nodesetup-apply.log` beside the binary.

**Health gate + revert (slice 8b-1).** After relaunching, if an update was
actually applied the survivor polls `http://127.0.0.1:<port>/api/version` (port
from `nodesettings.json`; unauthenticated, and the node binds `ListenAnyIP` so
loopback reaches it) for up to 60 s. If it never answers, it **rolls back**:
stop the bad node (`sc stop`, or `Kill()` just the node process on the direct
path — children/games survive), quarantine the bad binary as `.failed`, restore
`.old` → live, relaunch the previous binary, and best-effort re-confirm. New
exit codes 5 (rolled back) / 6 (rollback failed). The forward swap **consumes
`.new` by rename**, so a failed binary is `live`, not a lingering `.new` —
nothing re-applies, and the survivor reverts once and exits.

### `--self-update-dry-run` harness (`GSM.Node\SelfUpdateDryRun.vb`)

Exercises the whole path with no Manager and no hand-driven HTTP, modelled on
`--shim-self-test` / `--shim-reconnect-test`:

- `GSM.Node --self-update-dry-run --stage-only` — stages the running binary as
  `GSM.Node.new` through the real begin/chunk/commit code and stops. Apply by
  restarting the node (systemd's `ExecStartPre` swaps on the next start).
- `GSM.Node --self-update-dry-run` — same staging, then POSTs apply-update to
  the running node over loopback (reading port + token from
  `nodesettings.json`), triggering the real graceful update-exit → survivor swap
  → relaunch → re-adopt.

Transcript goes to console and `self-update-dryrun-result.txt`. The byte-
identical payload is fine — this proves swap/relaunch/re-adopt mechanics, not
version detection (slice 7).

### Commit-time OS guard + the systemd marker (slice 8)

**8a — commit-time OS-match guard (`SelfUpdate.vb` `CommitAsync`).** Before
promoting `.part` → `.new`, after the SHA-256 verify, the node sniffs the staged
bytes' magic bytes (`DetectStagedFormat`: `0x7F ELF` / `MZ` PE) and **rejects a
recognized wrong-OS binary** — 422, delete the `.part`. Only a definite mismatch
is blocked; an unrecognized format passes (the health gate is the backstop). The
Manager already magic-byte-matches at file selection (`manager.md`), so this is
defense-in-depth for a direct-API or buggy caller that bypassed the UI.

**8b-2 node half — clearing the systemd marker (`NodeProgram.vb`).** The
`ExecStartPre` revert above keys off a `.update-pending` marker outliving a
start. The node clears that marker once it has been healthy: an
`ApplicationStarted` hook (`ScheduleUpdateMarkerClear`) fires a **named** async
delay (15 s — long enough that a come-up-then-crash still leaves the marker for
the next start to revert; named function, not an inline lambda, to dodge the VB
`Task(Of Object)` trap) then deletes `<node>.update-pending`. The delete is a
harmless no-op on every non-update start and on Windows / bare nodes (only the
systemd unit ever writes the marker). **Deployment:** the marker-write/revert
lives in the unit and the marker-clear in the node binary, so an existing
systemd node needs *both* a regenerated unit (re-run NodeSetup install / rewrite
`gsmnode.service` + `daemon-reload`) and the new node binary before 8b-2 is
live. **Status: built clean; runtime auto-revert not yet exercised on a live
node.**

### Files

- `GSM.Node\SelfUpdate.vb` — `SelfUpdateService` (staging session + survivor
  routing + exit orchestration + Windows detached-spawn interop) and the
  staging/apply DTOs; **slice 8a** commit-time OS-match guard
  (`DetectStagedFormat`).
- `GSM.Node\SelfUpdateDryRun.vb` — the dry-run harness.
- `GSM.Node\Endpoints\NodeEndpoints.vb` — the four `/api/system/staged-binary`
  + `apply-update` routes in `SystemEndpoints`.
- `GSM.Node\NodeProgram.vb` — DI registration, the `--self-update-dry-run`
  dispatch, the post-`app.Run()` exit-code (read from a pre-`Run` reference),
  and **slice 8b-2** the `.update-pending` marker-clear on `ApplicationStarted`.
- `GSM.NodeSetup\ServiceManager.vb` — `BuildSystemdUnit` (the `ExecStartPre`
  apply-or-revert, `Type=notify`, `StartLimit*`) + `StartWindowsService` /
  **slice 8b-1** `StopWindowsService`.
- `GSM.NodeSetup\SelfUpdateApply.vb` — the universal survivor; **slice 8b-1**
  health-gate poll + auto-revert.
- `GSM.NodeSetup\Program.vb` — `--apply-update --wait-pid` parsing + dispatch.

### Shim survival across the update-exit (detach must suppress the node's own exit handling)

The whole self-update leans on 8-1's shims keeping the games alive while the node
is down. That nearly didn't hold. A deliberate `Detach` — the clean-shutdown
hook `DetachShimsForShutdown` sends one to every shim, so each keeps its game and
waits for the next node — closes the node↔shim pipe, and the node-side
`ShimSession` read loop saw that drop and treated it as a game exit: it routed
into `HandleProcessExited`, which (because a detach never sets
`StopIntentPending`) disposed the session (`Dispose` → `TryKillShim` →
`Kill(entireProcessTree:=True)`, killing the live shim **and the game under
it**) and then scheduled a crash-restart. Net effect on a self-update: the game
vanished the instant the node began its update-exit (while the node was still
running), a throwaway restart fired, then the real swap+relaunch landed on the
wrong process.

Fix, in `ShimSession`: a deliberate detach — `SendDetachAsync` (the shutdown
hook) and `DetachAsync` (the reconnect self-test) — sets a `_detaching` flag and
clears `_ownsShim` **before** the Detach frame is sent; `SignalExit` then skips
the `onExited` cascade when `_detaching` is set. So the post-detach link drop is
benign: no tree-kill, no crash-restart, the game is left running, and the next
node re-adopts it by saved endpoint.

**Lesson:** when the node deliberately detaches from a shim, it must suppress its
own exit/crash handling for that session, or it tears down and crash-restarts the
very game it just chose to leave running. This was latent on both platforms —
Linux escaped it only because a bare node exits promptly enough that the read
loop never runs the cascade, whereas a Windows node lingers in graceful shutdown
(a live SSE log stream holding Kestrel open to its ~30s drain timeout) long
enough to hit it every time. Verified fixed on all four survivor paths.

## Shim rediscovery + `node.db` hardening (Phase 8-3)

> Status: implemented and **builds clean on all targets; runtime verification
> deferred** (no live sweep/corrupt-db run yet). See `Phase8-3_Plan.md`.

8-1 (per-instance shim) and 8-2 (self-update) both lean on re-adoption: a
relaunched node re-attaches to the still-running shims by reading each instance's
saved `ShimEndpoint` (+ recovery payload) from `node.db`. That made `node.db` a
**single point of failure** — wipe or corrupt it while games run and they keep
running (the shim holds them) but become orphans: the node reports them Stopped,
crash detection is dead, and a Start spawns a duplicate that dies on port-in-use.
8-3 makes `node.db` a **cache, not a source of truth**.

### The endpoint is a pure function of the instance id

`ShimSession.MakeEndpoint` already derives the address from the id alone:

```
Windows → pipe:powergsm-shim-<sanitizedId>
Linux   → unix:<DataDirectory>/shims/<sanitizedId>.sock
```

So the stored `ShimEndpoint` is redundant, and — more usefully — every live shim
listens at this well-known pattern. The node can therefore ask the OS "what
shims are running right now?" with zero `node.db` involvement. `SanitizeId` is
**lossy** (non-alphanumerics collapse to `-`), so the pipe/socket name can't be
reversed to the true id; the shim reports its own id (and tail paths) in the
handshake instead.

### Snapshot pass first (full payload), then sweep for gaps (lean)

`NodeProgram` calls `pm.AdoptSnapshots()` then `pm.SweepAdoptLiveShims()`, in
that order, inside the same startup try-block:

- **`AdoptSnapshots`** runs first and unchanged. Every instance it adopts from
  `node.db` keeps the **full recovery payload** — `ExePath`/args/cwd (so
  crash-restart can rebuild a `SpawnSpec`), parse rules, crash policy, log
  paths.
- **`SweepAdoptLiveShims`** then enumerates the shim namespace
  (`EnumerateShimEndpoints`: `Directory.GetFiles("\\.\pipe\")` filtered to
  `powergsm-shim-*` on Windows, `*.sock` in `_shimSocketDir` on Linux) and
  **lean-adopts only live shims whose id is not already in `_instances`**. It
  always runs — dedup by id makes it a cheap no-op on already-adopted ids — so
  it also catches a shim the snapshots never knew about (e.g. a snapshot row
  that failed to adopt while its game is alive).

### Probe, then adopt

The sweep does a lightweight **probe** per endpoint first
(`ShimSession.ProbeEndpointAsync`: connect → Hello/HelloAck → read → close,
time-boxed by a `CancellationTokenSource`, never throws, returns a
`ShimProbeResult` of id + game pid/state + shim pid/version). For a live game
(`GameState = "running"`) whose id isn't already adopted, it builds the
`ManagedInstance` and calls the **existing** `AdoptViaShimAsync(managed,
endpoint)` — a second connect. Two connects per shim is a one-time startup cost
on a handful of instances, and it avoids reordering `AdoptAsync`'s
construct-with-known-id shape. The probe connects and drops cleanly; the shim
treats it as a brief node connection, keeps its game, and loops back to accept
the real adopt.

A probe that doesn't answer is treated as dead and skipped. The sweep
deliberately does **not** unlink stale Linux `.sock` files: a probe timeout
can't be safely told apart from a slow-but-live shim, and unlinking a live
shim's socket would orphan it. `UnixSocketListener` already clears a stale
socket at **bind time** when that instance next starts, so a dangling file is
harmless and self-heals on reuse.

### Lean adopt: what a node.db-less instance gets (`TryLeanAdoptShim`)

With no snapshot, the instance is rebuilt from `(instanceId, endpoint, live
gamePid)` learned over the handshake. The lean adopt:

- **registers EventStore with an EMPTY rule set** —
  `RegisterInstance(id, New List(Of LogParseRule)(), hydrateState:=True)`. This
  is load-bearing: `EventStore.UpdateParseRules` ignores an unregistered
  instance (logs "unregistered instance — ignored" and drops the push), so
  without a pre-registration the Manager's reconnect rule re-push would land on
  nothing. `hydrateState:=True` rehydrates the persisted match/tile state.
- **recovers the log paths from the shim** (see below) and starts file tailers
  (`StartFileTailers(..., skipResume:=True)`), flipping `CaptureStdout` off
  since the file is now authoritative.
- sets **`CrashPolicy = NeverRestart`** and leaves **`StartInfo = Nothing`**.

What works after a lean adopt: status, graceful stop, stdout/exit relay, and
file tailing — so player/chat/server-state tracking resumes **go-forward** — and
within ~3s the real parse rules, via the Manager's existing stream-health
reconnect re-push (`EnsureLogStreamAsync` → `ReregisterParseRulesAsync` →
`UpdateParseRulesAsync`, originally built for the node-update case). No new
"force rules" endpoint or Manager change was needed.

**Residual gap (by design):** the lean path has no `StartInfo`, so a crash can't
rebuild a `SpawnSpec` — `CrashPolicy = NeverRestart` makes a post-lean-adopt
exit leave the game Stopped rather than attempt (and fail) a restart
(`RestartInstanceAsync` also guards `StartInfo Is Nothing` → `CrashLoopHalted`,
belt-and-suspenders). Closing this is the remaining Tier-3 work: the shim echoes
its full `SpawnSpec` so the node can rebuild `StartInfo` with zero `node.db`.
Deferred.

### Log-path recovery via the shim (the shim is the cache)

The tail path is `f(InstallPath, pluginPattern, InstanceId)` — e.g. LO's
`{InstallPath}/Mist/Saved/Logs/{InstanceId}.log`. The node has only the id; the
install path and the plugin-specific relative shape both live on the **Manager**
(plugins only run there), so the node **cannot** derive the path alone, even
with the working directory. Rather than depend on the Manager being up, the
**shim carries it**:

- `SpawnSpec.LogFilePaths` — the resolved absolute paths the node already
  computes at start — is handed to the shim at spawn (`StartViaShimAsync`).
- the shim stashes it (`Supervisor._logFilePaths`, set in `HandleSpawnAsync`)
  and echoes it in `HelloAckMessage.LogFilePaths` on **every** later handshake.
- on a lean adopt the node reads `ShimSession.AdoptedLogFilePaths` and starts
  tailers — `node.db`- **and** Manager-independent.

The shim never tails the files; it only remembers the strings so a node that
lost its `node.db` can recover where to look. Both fields are append-only on the
wire (no `ProtocolVersion` bump). A **pre-8-3 shim** answers the handshake
without `InstanceId`/`LogFilePaths`, so the sweep logs "older shim" and skips it
— only relevant on the `node.db`-loss path, since the snapshot path still adopts
older shims by their saved endpoint. To exercise the sweep you must redeploy the
shim and start the instance under the new build.

**Caveat (shared with a normal adopt, not a regression):** adopt tailers start
with `skipResume:=True` (seek to end), so log-path recovery restores go-forward
tracking, not a historical rebuild of the current player list — players already
connected before the bounce repopulate as they next chat/leave/rejoin. Identical
to a normal node restart today.

### Corrupt `node.db` self-heals instead of crash-looping

A damaged `node.db` previously threw out of `Main` before `app.Run()`, taking
the whole node down on startup — and under systemd, crash-looping until
`StartLimit` gave up. `NodeDatabase.EnsureCreated` now wraps the original DDL
body (moved verbatim into `EnsureCreatedCore`) in a guard:

- On `SqliteException` whose `SqliteErrorCode` is `11` (`SQLITE_CORRUPT`) or
  `26` (`SQLITE_NOTADB`) **only**, `BackupAndDeleteCorruptDb` runs:
  `SqliteConnection.ClearAllPools()`, then `File.Move` the bad file to
  `node.db.corrupt-<yyyyMMdd-HHmmss>` (recorded in `LastCorruptionBackup`;
  falls back to delete if the move fails), then best-effort delete of the
  `-wal`/`-shm`/`-journal` sidecars so a half-written WAL can't re-corrupt the
  fresh file. `EnsureCreatedCore` is then retried once on the empty file.
- Any **other** `SqliteException` (locked, busy, readonly, …) is not corruption
  and propagates unchanged — we only swallow the two codes that mean "this file
  is not a usable database".

`NodeDatabase` is constructed before DI, so it has no logger; it stashes the
backup path and `NodeProgram.Main` logs the reset at **Warning** once the logger
exists (right after the startup version line, before the adopt/sweep block). The
lost rows are all node-local cache — instance snapshots, crash history, the chat
mirror, tailer cursors — which the Manager re-pushes and the sweep rediscovers.

**The two halves compose:** a corrupt `node.db` resets to empty, the snapshot
pass finds nothing, and the sweep re-adopts the live shims straight from the OS
namespace. Net result of the whole phase: **a lost or corrupt `node.db` resets
quietly and orphans nothing.**
