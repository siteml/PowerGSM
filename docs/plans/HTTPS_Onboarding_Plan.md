# PowerGSM — HTTPS Implementation & HTTP→HTTPS Onboarding (0.7.0)

Status: IMPLEMENTED + hardened (2026-07-24). Verified working on win + linux
nodes. **Superseded 2026-10 by `HTTPS_SinglePort_Plan.md`**: the two-port
model (`TlsPort = ListenPort+1`) is now the legacy layout, HTTPS shares the
listen port via a sniffing middleware, HTTP-off is the secured end state, and
the deferred items below (fleet enable, token rotation, NodeSetup HTTPS) are
done or tracked there. Kept for the original design rationale.
Original plan retained
underneath. (Filed into docs/plans 2026-08-02; canonical copy is this one —
the claude project copy mirrors it.)

## Implemented — file map

- **GSM.Contracts/NodeApiContract.vb**: NodeVersionResponse += TlsSupported/TlsEnabled/TlsPort/CertFingerprintSha256; new EnableTlsRequest/EnableTlsResponse/TlsStatusResponse (incl. HttpMode)/SetHttpModeRequest; INodeClient += EnableTlsAsync/GetTlsStatusAsync; NodeErrorCodes += TlsNotSupported/TlsAlreadyEnabled/CertGenerationFailed (appended at enum end).
- **GSM.Node/TlsCertificateService.vb** (new, namespace GSM.Node.Security): ECDSA P-256 self-signed cert, 10y, CN=NodeId, SAN best-effort; PFX + random pwd at `<DataDirectory>/tls/node.pfx[.pwd]`, 0600 on Linux; regenerates when expired/≤30d (logged loudly — Manager will demand re-trust). **LoadPfx: Windows loads with MachineKeySet|PersistKeySet (fallback UserKeySet) — SChannel CANNOT serve TLS from EphemeralKeySet keys (bug found in the field: Windows nodes' HTTPS listeners failed all handshakes while Linux/OpenSSL worked); EphemeralKeySet is Linux-only.**
- **GSM.Node/NodeProgram.vb**: NodeConfiguration += EnableTls, TlsPort (0→ListenPort+1 via EffectiveTlsPort), HttpMode ("Open"/"LoopbackOnly"/"Disabled"; Disabled coerced to Open unless EnableTls). Kestrel: HTTP listener per HttpMode + HTTPS listener when EnableTls (cert failure logged, HTTP survives). TlsCertificateService eager + in DI.
- **GSM.Node/SelfUpdate.vb**: SelfUpdateService.RequestRestart() — plain restart, no staged binary (systemd non-zero exit, else NodeSetup relauncher's nothing-staged relaunch path). Returns False if no vehicle → caller must not stop host.
- **GSM.Node/Endpoints/NodeEndpoints.vb**: /api/version advertises TLS fields. Authenticated `GET /api/tls/status` (incl. httpMode), `POST /api/tls/enable` (409 if on; cert generated before reply so fingerprint == listener cert; config write via NodeSettingsFile temp+replace; reverts if no restart vehicle), `POST /api/tls/http-mode` (Open/LoopbackOnly/Disabled; Disabled rejected unless TLS on; persists + restarts). NodeSettingsFile: WriteTlsSettings + WriteHttpMode.
- **GSM.Manager/Data/GsmDbContext.vb**: NodeEntity += UseTls, TlsPort, PinnedCertFingerprint(128), TlsState(32: None/Enabling/Active/FingerprintMismatch). Migration `AddNodeTls` created + applied.
- **GSM.Manager/Core/NodeHttpClient.vb**: CreateHandler() = single transport-trust source (SHA-256 pin, FixedTimeEquals, chain/hostname ignored); CreateOneShotClient() for push/upload/mapgen; NodeCertificateException (subclass NodeConnectionException) from AuthenticationException detection in WrapException; EnableTlsAsync/GetTlsStatusAsync/SetHttpModeAsync (last one concrete-class-only). Factory: TlsSettingsResolver consulted every GetClient (all legacy call sites TLS-aware untouched); transport-signature eviction; CreateExplicitClient; **downgrade guard: resolver exception returns the cached client instead of silently rebuilding on HTTP — a DB hiccup can never leak the token in cleartext for a node already on pinned HTTPS**.
- **GSM.Manager/ManagerProgram.vb**: TlsSettingsResolver → DB lookup; TlsOnboardingService in DI.
- **GSM.Manager/Core/TlsOnboardingService.vb**: ProbeAsync; EnableTlsAsync (TOFU pin from authed-HTTP enable response); VerifyAndActivateAsync (90s pinned poll; mismatch aborts; timeout retryable); AdoptExistingTlsAsync; RollbackToHttp (keeps pin); MarkMismatch; GetReportedFingerprintAsync; RetrustAsync; **GetHttpModeAsync + SetHttpModeAsync (over pinned HTTPS only; waits for restart)**.
- **GSM.Manager/UI/UiPanels.vb** NodePanel: HTTPS row shows port + pin tail + HTTP restriction state; primary link (Enable/Verify/Adopt/Re-trust/Switch back to HTTP) + secondary link (**Restrict HTTP to localhost... / Re-open HTTP...**); rollback has a lockout guard (refuses while HttpMode ≠ Open — re-open HTTP first); cert-mismatch on version fetch → MarkMismatch.
- **Directory.Build.props**: 0.7.0.

## Security posture summary

Active TLS = all Manager→node traffic (token, RCON, uploads, SSE) encrypted + server-authenticated via pin. Remaining plaintext surfaces: pre-onboarding traffic, and the node's open HTTP listener until restricted via the panel's "Restrict HTTP to localhost". Recommended sequence per node: enable HTTPS → verify → Restrict HTTP → rotate AuthToken (any token that ever traveled plaintext is burned). /api/version stays unauthenticated on both listeners by design (fingerprint is public under pinning).

## Still deferred (OPEN work)

NodeSetupForm HTTPS option for brand-new nodes; fleet-wide enable; user-supplied certs; cert-rotation UX; node import/export (design notes agreed: export must carry UseTls/TlsPort/PinnedCertFingerprint since adopt-fallback dies once HTTP is restricted; DPAPI Steam creds don't port; keep original IDs; export file carries AuthToken → treat as secret). ProtocolVersion NOT bumped (all additive).

## EventStream interaction (noted 2026-08-02)

None structural. TLS is transport: the planned `/events` + SSE endpoints and
the Manager→node PlayerIdentity append ride the same pinned NodeHttpClient
handler automatically. The standing rule applies: any new stream client code
must use CreateOneShotClient()/CreateHandler(), never an ad-hoc HttpClient.

## Field notes

- Windows SChannel + EphemeralKeySet = silent TLS-handshake failure with healthy-looking process; symptom is onboarding stuck at "Enabling — waiting for node" while HTTP works. Fixed via persisted key store (see TlsCertificateService.LoadPfx).
- Same symptom's other cause: firewall not allowing TlsPort (default 8766) inbound. Distinguish on the node: `curl -k https://localhost:8766/api/version` (works locally = firewall; fails locally = cert).

---

# Original plan (as approved)

## Trust model decision

**Self-signed per-node certificate, SHA-256 fingerprint pinned by the Manager (trust-on-first-use).** Chain/hostname deliberately ignored — the pin is the trust anchor, like SSH host keys. Fingerprint change after pinning = hard failure + explicit user re-trust.

## Compatibility invariants

1. Old Manager + new Node: node keeps plain HTTP on ListenPort by default — zero behavior change.
2. New Manager + old Node: /api/version lacks TLS fields → deserializes False/0/Nothing → stays HTTP.
3. TLS enabled: HTTPS on second port (default ListenPort+1); HTTP stays up during transition so rollback is trivial.
4. Contracts changes additive only.

## Onboarding flow

Probe → (update node if pre-0.7.0) → POST /api/tls/enable over authenticated HTTP → pin fingerprint from response → node restarts → Manager verifies over pinned HTTPS → UseTls=True. Rollback = flip back (HTTP listener still up). Later hardening: HttpMode=LoopbackOnly on the node.

## Known risks (tracked)

- Enabling TLS bounces the node (Kestrel can't add listeners at runtime); shim-mode instances survive, pre-shim stdout instances lose stdio (same as self-update). `/api/tls/enable` and `/api/tls/http-mode` are NOT gated on active installs — apply during quiet periods.
- A future missed ad-hoc HttpClient in NodeHttpClient would break only against TLS nodes — always use CreateOneShotClient()/CreateHandler().
