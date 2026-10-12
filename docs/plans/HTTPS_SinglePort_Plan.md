# PowerGSM — Single-port HTTPS, HTTP-off-by-default, cert-at-setup (0.7.x)

Status: `[active]` — plan written 2026-08-30; **slices 0/1/3/4 live in
production as of 2026-10-11 (interim stamp 0.6.5); slice 2 landed in the
tree 2026-10-11 evening (0.6.6) — live-validated 21:32 — ALL FIVE SLICES DONE; remaining: revert stamps to
0.7.0, commit, release.**
**Release gate for 0.7.0**: all five slices ship together (operator decision
2026-10-10 — no partial cut).
Supersedes the "Still deferred" list in `HTTPS_Onboarding_Plan.md` (NodeSetup
HTTPS for new nodes, fleet-wide enable) and changes the port model.
User-supplied certs and cert-rotation UX stay deferred (noted at the end).

### Slice status (as of 2026-10-11)

| # | Scope | Status |
|---|-------|--------|
| 0 | NodeSetup TLS-key round-trip bug + preserve-unknown-keys save | **done** — `NodeSetupConfig.Save` merges typed object over existing JSON (see its doc comment) |
| 1 | Sniffing middleware + single-port listener + `SinglePort` field + startup refuse rule | **DONE — live-validated 2026-10-10** on Windows (service + user-mode) and Linux (MsiProx-Factorio): shared port serving pinned HTTPS with HttpMode=Disabled across the whole fleet |
| 2 | `TlsCertificateService` → Contracts + NodeSetup wizard/menu/auto-init + Linux chown | **DONE — LIVE-VALIDATED 2026-10-11 21:32** (second node on the dev box, port 8775, Windows service: NodeSetup GUI → HTTPS on / plain HTTP Disabled → Add Node with the printed fingerprint → "Connected! Node: SITE-MAIN" over pinned HTTPS with no Secure step. First Test Connection failed until a service restart — likely service started before the Transport save; node log unchecked.) — `GSM.Contracts/TlsCertificateService.vb` (namespace unchanged `GSM.Node.Security`; ILogger replaced by two optional callbacks since Contracts is dependency-free; adds `HasPersistedCertificate`, `TryReadPersistedFingerprint`, `RegenerateCertificate`, `TlsDirectory`, `FormatFingerprintForDisplay`). Node constructs it with logger lambdas. NodeSetup: `ConfigHelpers` transport validators + `NormalizeTransport` (TlsPort=ListenPort→0, HttpMode canonical, no-TLS→Open) + `EnsureTlsCertificate` / `ReadTlsFingerprint` / `RegenerateTlsCertificate` / `BuildManagerHandoffLines` (relative DataDirectory rebased to BaseDirectory like the node); console wizard step 5 "Transport" (HTTPS default **on** for a fresh file, plain HTTP None/Localhost/Open default None, advanced separate port), save mints the cert and prints token + fingerprint + URL block; main menu 9 "Show TLS fingerprint" (offers to mint) / 10 "Regenerate TLS certificate" (warns re-trust, stop node first); edit menu 9 Transport; GUI "Transport" tab (checkbox, mode combo, port, fingerprint box, Create/Copy/Regenerate; Save mints + shows fingerprint in the saved dialog); `--auto-init` defaults `--tls` + HttpMode Disabled, `--no-tls`, `--http-mode <m>`, prints the handoff block. Linux chown: the cert is minted before the existing service-user chown of DataDirectory, so `<data>/tls` is covered by that pass (no new ServiceManager code). Validated as above. |
| 3 | Manager: `IsUsable` relax, Add-Node fingerprint, panel wording | **DONE — live 2026-10-10 (0.6.2+)**; Add-Node *with* fingerprint still unexercised (needs slice 2 to mint a cert pre-enable) — `NodeTlsSettings.IsUsable` drops the port requirement (+ `EffectivePort`), `TlsOnboardingService` dials `EffectiveTlsPort` everywhere, `NodeSetupForm` "Cert SHA-256" field (Add: pinned-first-contact, `TlsPort=0`; Edit: display-only), NodePanel "shared port N / alternate port N / HTTP: off" |
| 4 | Guided "Secure this node" + fleet action + Diagnostics column | **DONE — live-validated 2026-10-10/11 (0.6.3/0.6.4)** — `TlsOnboardingService.SecureNodeAsync` (probe → enable/adopt → verify → HttpMode=Disabled, idempotent per step, progress callback); NodePanel primary link "Secure this node…" with "Advanced: enable HTTPS only…" secondary; Nodes → "Secure All Nodes (HTTPS)…" (`SecureNodesForm`: checklist of not-yet-secured nodes, running-instance count, sequential run, per-row result); Diagnostics TLS column. **Rotate token (0.6.4, late 2026-10-10):** `POST /api/auth/rotate` — HTTPS-only (403 on plaintext), Manager-generated 64-hex token, node persists via `NodeSettingsFile.WriteAuthToken` + restarts. `TlsOnboardingService.RotateTokenAsync` stores the new token locally *first*, reverts on refusal, and on a lost reply probes which token the node restarted with. Final step of `SecureNodeAsync` (`rotateToken:=True`), fleet form checkbox (default on; ticked ⇒ already-secured nodes are listed for a rotation-only pass), panel link "Rotate auth token…". Other Managers must re-enter the token (Edit Node). **LIVE-VALIDATED 2026-10-11 00:11** on a fresh service-mode Windows node (MAIN-HTTP): added over plain HTTP (no fingerprint) → Update Nodes to 0.6.4 → "Secure this node" ran enable → verify → HTTP off → rotate in one click. Earlier the same night: six-node fleet secured in one fleet-form pass on 0.6.3 (no rotation yet). |

Also landed 2026-10-10 (found live, evening): **in-place port move** for
nodes onboarded on a dedicated alternate port before slice 1. Node:
`POST /api/tls/enable` while already enabled with a *different* effective
port = move (same cert, persist `TlsPort`, restart; same port = 409
`TlsAlreadyEnabled` as before). Manager: `MoveTlsToSharedPortAsync` (sends
`TlsPort=0` over the current pinned channel, pins unchanged, re-verifies on
the listen port); node panel link "Move HTTPS to shared port…" when
`TlsPort` ≠ listen port; `SecureNodeAsync` does the move automatically for
Active alt-port nodes, and the fleet form lists alt-port nodes as
not-yet-secured. Needs the **node** rebuilt too — an older node answers 409
and the Manager says so. **LIVE-VALIDATED 2026-10-10 21:44** on win-test
(0.6.2 Manager + node): alt port 8766 → shared 8765 in one bounce, pin
unchanged, panel reads "active on shared port 8765". This is also the first
production proof of the slice-1 sniffing listener serving a pinned Manager.
**Secured nodes rolled back on update (found 2026-10-11 08:42, win-test, fix in 0.6.5):** NodeSetup's survivor health gate polled `http://127.0.0.1:<port>/api/version` only; with HttpMode=Disabled the sniffing listener drops plaintext at byte 1, the 60 s gate times out and `.old` is restored — a healthy update reverted. `SelfUpdateApply.WaitForHealthy` now tries http and https (cert validation off, loopback) each round. Rollout is self-healing because "all components" swaps NodeSetup in place *before* the node bounce. Linux/systemd unaffected (no survivor gate). **LIVE-VALIDATED 09:03** — win-test (HTTP off) updated to 0.6.5, `nodesetup-apply.log` shows `health check passed via https://`.
**Fresh-enable false MISMATCH (found 22:07, Trigkey + SER8, fix in tree):**
`NodeHttpClient.WrapException` classified *every* `AuthenticationException`
as a pin failure, including "unexpected packet format" from dialling HTTPS
at the old plaintext process during the ~30 s restart gap, so
`VerifyAndActivateAsync` aborted as MITM; the node log proved one cert, never
regenerated. Now the pin callback flags a real rejection and only that maps
to `NodeCertificateException`; other handshake failures are
`NodeConnectionException` and the verify loop keeps polling. Same evening: Edit Node's Test Connection dialled
TLS on the listen port instead of the recorded `TlsPort` (false "certificate
validation failed"); `GetHttpModeAsync` now marks `FingerprintMismatch` on a
pin failure so the panel shows Re-trust instead of "HTTP still open".

Also landed 2026-10-10 (found live): re-trust/adopt fingerprint fetch falls
back to an unpinned HTTPS handshake (`NodeHttpClient.ProbePresentedFingerprintAsync`)
when the authenticated HTTP path is unavailable — closes the mismatch +
restricted-HTTP lockout. Compile + live gates for all of the above still owed.

Update this table as slices land; it is the only place slice progress is
recorded.

## Why

1. Operator rule: **HTTP is all-or-nothing.** A node is either fully plaintext
   (pre-onboarding / legacy) or fully HTTPS with no plaintext surface. The
   current two-listener model leaves HTTP up "for rollback" and asks the
   operator to remember to restrict it.
2. The second port (`ListenPort+1` default) is an artifact of Kestrel needing one
   listener per protocol. It costs a firewall rule per node, a second port
   concept in the Manager (`TlsPort`, `NodeTlsSettings.IsUsable` gate), and a
   port that NodeSetup doesn't know exists.
3. New nodes should never speak plaintext at all: NodeSetup can mint the cert
   and print the fingerprint next to the token; the Manager pins it
   out-of-band on Add Node. TOFU-over-plaintext becomes the *legacy* path.
4. Found while planning: `NodeSetupConfig.NodeSection` lacks
   `EnableTls/TlsPort/HttpMode`, and `Save` serializes the whole typed object →
   **any NodeSetup save after TLS onboarding silently strips TLS** and the node
   comes back HTTP-only on next restart. Real bug today, fixed in slice 0.

## Decisions

- **D1 — One port, protocol-sniffed.** `ListenPort` serves both. A Kestrel
  connection middleware peeks the first byte: `0x16` (TLS record type
  Handshake) → TLS; anything else → plaintext or reject per `HttpMode`.
  `TlsPort` stays in config as an *optional alternate* (if > 0 and ≠ ListenPort
  an additional HTTPS-only listener is opened there, for operators who want
  the split). Default `0` now means "same port", not "+1".
- **D2 — `HttpMode` keeps its three values, redefined for one port.**
  `Open` = plaintext accepted from anywhere (transition/legacy). `LoopbackOnly`
  = plaintext accepted only when `RemoteIpAddress.IsLoopback`. `Disabled` =
  connection closed at byte 1 — bytes never reach auth middleware, no token
  can leak. `Disabled` still requires `EnableTls` (existing guard).
- **D3 — Token stays.** Cert+pin authenticates the node to the Manager; the
  Bearer token authenticates the Manager to the node. Mutual TLS (Manager
  client cert pinned by node) is a possible later slice; it interacts with
  NodeTransfer (bundle must carry/rotate Manager identity) and the ownership
  lease, so it is NOT in this plan.
- **D4 — Cert generation moves to `GSM.Contracts.Security` (decided
  2026-08-30).** `TlsCertificateService` is DI-free (`New(dataDirectory,
  ILogger)`). NodeSetup references Contracts only and must not reference the
  GSM.Node exe (drags the web-host SDK into the setup tool; setup runs before
  the node exists). Contracts chosen over a new `GSM.Node.Common` because a
  new assembly would have to ship in the node zip, the NodeSetup zip and the
  8-2 co-update bundle for one class; precedent = `BuildInfo` and the Slice 3
  `INodeClient` ruling ("in Contracts, not plugin-facing, not governed by
  ContractsVersion") — same doc-comment goes on this class. Copy-pasting into
  NodeSetup rejected (duplicates the SChannel MachineKeySet gotcha).
- **D5 — Fresh-node default = HTTPS-only.** NodeSetup wizard defaults
  `EnableTls=True, HttpMode=Disabled`, generates the cert into
  `<DataDirectory>/tls/` during the wizard, and prints the SHA-256 fingerprint
  in the same block as the token. Operator can opt down to `Open` in the
  wizard (legacy Manager, or they intend TOFU).
- **D6 — Manager Add-Node gains an optional fingerprint field.** Present →
  node row is created with `UseTls=True, TlsState=Active, PinnedCertFingerprint
  = value, TlsPort = Port`; first request is already pinned HTTPS. Absent →
  existing HTTP-first behaviour (probe → TOFU) unchanged.
- **D7 — Compat invariants (unchanged from the 0.7.0 plan):** old Manager +
  new node in `Open` works (plaintext still accepted on ListenPort); new
  Manager + old node (two-port) works (node reports `TlsPort = ListenPort+1`,
  Manager uses whatever port the node reports); Contracts additive only;
  ProtocolVersion not bumped.
- **D8 — HTTP/1.1 only on the sniffed listener.** The node is 1.1 (SSE, chunked
  upload). No ALPN/h2 negotiation in the custom SslStream path. If h2 is ever
  wanted, use the alternate `TlsPort` listener via Kestrel's native `UseHttps`.

## Node — sniffing middleware

`GSM.Node/Security/TlsSniffMiddleware.vb` (new). Installed via
`options.ListenAnyIP(ListenPort, Sub(l) l.Use(Function(next) TlsSniff(next, ...)))`.
`ListenOptions.Use(Func(Of ConnectionDelegate, ConnectionDelegate))` is public
API; Kestrel's own `HttpsConnectionMiddleware` is internal so the TLS half is
ours:

1. `Dim result = Await ctx.Transport.Input.ReadAsync(ct)`; if buffer empty and
   completed → return. Read first byte, then **`AdvanceTo(buffer.Start)`** so
   nothing is consumed.
2. `first = &H16` →
   - `Dim ssl As New SslStream(New DuplexPipeStream(ctx.Transport), leaveOpen:=False)`
     — need a `Stream` over the `IDuplexPipe`. Write a small
     `DuplexPipeStream` (Read → `PipeReader.ReadAsync`+`AdvanceTo`, Write →
     `PipeWriter.WriteAsync`, Flush → `FlushAsync`). ~80 lines.
   - `Await ssl.AuthenticateAsServerAsync(New SslServerAuthenticationOptions With {
       .ServerCertificate = cert, .ClientCertificateRequired = False,
       .EnabledSslProtocols = Tls12 Or Tls13, .ApplicationProtocols = {Http11}})`
     with a handshake timeout (10 s) — abort connection on timeout/failure.
   - Replace transport: `ctx.Transport = New DuplexPipe(PipeReader.Create(ssl), PipeWriter.Create(ssl))`
     (`PipeReader.Create(Stream)` / `PipeWriter.Create(Stream)` are public in
     .NET 8; a trivial `IDuplexPipe` record class wraps them).
   - Set `ctx.Features.Set(Of ITlsConnectionFeature)(New TlsFeature With {.ClientCertificate = Nothing})`
     and `ITlsHandshakeFeature` (protocol/cipher from `ssl`) so
     `HttpContext.Request.IsHttps` is truthful and logs can show the protocol.
   - `Await next(ctx)`; dispose ssl in Finally.
3. `first <> &H16` →
   - `HttpMode = Disabled` → `ctx.Abort()` (log at Debug with remote IP; rate
     tracker not involved — nothing parsed).
   - `LoopbackOnly` and remote is not loopback → `ctx.Abort()`.
   - else → `Await next(ctx)` unchanged (plaintext).
4. Cert failure at startup: today the HTTPS listener is skipped and HTTP
   survives. New rule: if `EnableTls` and cert load fails → **node refuses to
   start** when `HttpMode = Disabled` (a silent fallback to plaintext is the
   exact thing D2 forbids); when `Open/LoopbackOnly` → log loudly, serve
   plaintext only, `/api/version` reports `TlsEnabled=False` so the Manager's
   probe shows the failure.

Kestrel block in `NodeProgram.vb:389-424` becomes: one `ListenAnyIP(ListenPort)`
with the sniffer when `EnableTls`, else the existing HttpMode select (no
sniffer needed for a plaintext-only node — keeps the legacy path byte-identical);
plus the optional alternate `TlsPort` listener with native `UseHttps` when
`TlsPort > 0 AndAlso TlsPort <> ListenPort`.

`/api/version` + `/api/tls/status`: `TlsPort` now reports `ListenPort` when
same-port (or the alternate when configured). Add additive field
`SinglePort As Boolean` to both responses so the Manager panel can say "HTTPS
on :8765 (shared)" vs "on :8766".

`/api/tls/enable`: `request.TlsPort` semantics → `0`/absent = same port. A
0.7.0 Manager sends empty request → gets same-port. A node that still wants
+1 must be told explicitly (`TlsPort = ListenPort+1`).

`EnsureDefaults`: `TlsPort = ListenPort` → normalise to 0.

## NodeSetup

Slice 0 (bug): `NodeSection` += `EnableTls As Boolean`, `TlsPort As Integer`,
`HttpMode As String = "Open"`. Round-trips existing files. Also make
`NodeSetupConfig.Save` preserve unknown keys under `Node` (read existing
JsonNode, overlay known props) so a future node-only key can't be stripped
again.

Slice 2 (wizard):
- New step **"5) Transport"** between authentication and review: "Use HTTPS
  (recommended)?" [Y] → `EnableTls=True`; "Allow plain HTTP? (None /
  Localhost only / Open)" default **None** → `HttpMode=Disabled`. Advanced:
  "Separate HTTPS port (blank = same as listen port)".
- On save with `EnableTls`: construct `TlsCertificateService(DataDirectory,
  logger)`, `GetOrCreateCertificate(NodeId)`, print fingerprint in the token
  block:
  ```
  Auth token:            <token>
  TLS fingerprint (SHA-256): ab12…   ← paste into Manager → Add Node
  Listens on:            https://<host>:8765  (plain HTTP: disabled)
  ```
- Main menu: "9) Show TLS fingerprint" (re-print without regenerating) and
  "10) Regenerate TLS certificate" (warns that the Manager will need re-trust).
- Windows GUI (`Windows\GuiBootstrap`) mirrors the step; if it lags, console
  is the reference and GUI gets a follow-up — GUI must at minimum not strip
  the keys (covered by slice 0 since it shares `NodeSetupConfig`).
- `--auto-init` gains `--tls` / `--no-tls` (default `--tls`) and prints the
  fingerprint after the token.
- Validation: `ValidateHttpMode`, `ValidateTlsPort` (0 or valid port, ≠
  ListenPort allowed, = ListenPort normalised to 0).

Windows key-store note: `LoadPfx` uses MachineKeySet — when NodeSetup runs the
generation as an admin user and the service later runs as a different account,
`MachineKeySet` is what makes it readable; keep that path. On Linux, NodeSetup
generates as the invoking user then `chown`s `<data>/tls` to the service user
in the existing "create service user" flow (add to `ServiceManager`).

## Manager

- `NodeTlsSettings.IsUsable`: drop the `TlsPort > 0` requirement; `effectivePort
  = If(tls.TlsPort > 0, tls.TlsPort, port)`. `TlsOnboardingService` stores
  `TlsPort` exactly as the node reports (unchanged) — same-port nodes report
  `ListenPort`, so all existing code paths keep working with no special case.
  `VerifyAndActivateAsync` guard `TlsPort > 0` → `>= 0`.
- Add Node dialog: optional "Certificate fingerprint (SHA-256)" textbox.
  Non-empty → normalise (strip colons/spaces, lowercase, must be 64 hex) →
  row created `UseTls=True, TlsState=Active, PinnedCertFingerprint, TlsPort=Port`.
  First contact is pinned HTTPS; mismatch → existing `FingerprintMismatch`
  handling. Empty → today's behaviour.
- NodePanel HTTPS row: show "shared port" vs alternate port; when
  `HttpMode=Disabled` show "HTTP: off". Primary action for a `Supported,
  NotEnabled` node becomes **"Secure this node…"** = one guided sequence:
  enable (same port) → verify → set `HttpMode=Disabled` → **prompt once at the
  end** to rotate the token (existing rotation flow; any token that travelled
  plaintext is burned). Individual links stay available under an "Advanced"
  secondary link for stepwise/rollback use.
- Rollback lockout stays: "Switch back to HTTP" refuses while
  `HttpMode ≠ Open` — but with one port, rollback = `SetHttpMode(Open)` then
  `RollbackToHttp`; same two steps, same guard.
- Fleet: Tools → "Secure all nodes…" iterates `Supported, NotEnabled` nodes
  sequentially with per-node result rows (reuses the guided sequence; skips
  nodes with running non-shim instances unless overridden — the restart
  drops pre-shim stdio, same caveat as today). **One up-front dialog** lists
  the candidate nodes as a checklist with two columns — "Secure" and "Rotate
  token" — **all checked by default**; operator unticks per node, no further
  prompts during the run. Rotation runs per node immediately after that
  node's `HttpMode=Disabled` succeeds, so a batch abort mid-way leaves
  already-secured nodes fully done.
- Diagnostics probe grid TLS column: add "shared"/"alt :N"/"HTTP off".

## Slices (each independently shippable)

| # | Scope | Node restart? | Gate |
|---|-------|---------------|------|
| 0 | NodeSetup TLS-key round-trip bug + preserve-unknown-keys save | no | Edit config via NodeSetup on a TLS node → keys survive; `nodesettings.json` diff = only edited keys |
| 1 | Sniffing middleware + single-port listener + `SinglePort` field + startup refuse rule | yes | `curl http://node:8765/api/version` and `curl -k https://node:8765/api/version` both answer in `Open`; plaintext gets connection reset in `Disabled`; SSE + chunked upload + RCON proxy work over sniffed TLS; loopback plaintext works in `LoopbackOnly` from the node, refused remotely; old two-port config (`TlsPort=8766`) still serves both ports |
| 2 | `TlsCertificateService` relocation + NodeSetup wizard/menu/auto-init + Linux chown | — | Fresh Linux + Windows node via wizard → never any plaintext accepted; fingerprint printed matches `/api/tls/status` |
| 3 | Manager: `IsUsable` relax, Add-Node fingerprint, panel wording | — | Add fresh node by fingerprint → Active on first contact; wrong fingerprint → Mismatch, no token sent (verify via node log: zero auth attempts) |
| 4 | Guided "Secure this node" + fleet action + Diagnostics column | — | Existing live nodes migrated one by one, then via fleet action on a test pair |

Slice 1 alone is deployable to existing nodes with no behaviour change while
`EnableTls=False` (sniffer not installed). Existing TLS nodes on `+1` keep
`+1` until re-enabled (or `TlsPort` edited to 0) — no forced migration.

## Test matrix (slice 1)

- TLS 1.2 and 1.3 ClientHello (curl `--tlsv1.2`, `--tls-max 1.2`).
- Garbage first byte / HTTP verb bytes in `Disabled` → reset, no log spam
  beyond Debug, rate tracker untouched.
- Slowloris on byte 1: `ReadAsync` bounded by a 10 s first-byte timeout →
  abort. (Kestrel's `RequestHeadersTimeout` doesn't cover pre-parse bytes in
  our middleware.)
- Handshake failure mid-way (client aborts) → no unobserved exception, no
  leaked SslStream.
- `MaxConcurrentConnections` still enforced (middleware runs after Kestrel's
  connection limit — verify ordering).
- Windows SChannel with `MachineKeySet` cert through our own `SslStream` (not
  Kestrel's) — re-verify the 0.7.0 field bug doesn't resurface via a different
  loader path.
- Shim survives node restart during enable (existing 8-1 guarantee).

## Non-goals / still deferred

- User-supplied certs (operator PFX / ACME) and cert-rotation UX with
  Manager-side re-trust prompt — separate plan; the sniffer is cert-agnostic.
- Mutual TLS replacing the token (D3).
- HTTP/2 on the shared port (D8).
- Node import/export TLS fields — belongs to NodeTransfer rung 1 (its plan
  already notes `UseTls/TlsPort/PinnedCertFingerprint` must ship in the
  bundle).

## Rules honored

VB.NET; Contracts additive only (`SinglePort` field, no removals); wire-compat
on all existing endpoints; ProtocolVersion/ContractsVersion unchanged; no
ad-hoc `HttpClient` — Manager paths continue through `CreateHandler()` /
`CreateOneShotClient()`; NodeProgram `reloadOnChange` untouched.
