# PowerGSM Reference — VB.NET Gotchas

Part of the PowerGSM reference set (index: [`../PowerGSM_Reference.md`](../PowerGSM_Reference.md)).
Language- and framework-level pitfalls hit while building PowerGSM, gathered
from across the codebase: the core quick-reference table plus the per-phase
"additions to the table" that accumulated during NodeSetup, cross-platform
hardening, shared-config (5h), connection bindings, and utility plugins.

> The contract-level reserved-keyword fixes (enum members renamed to dodge VB
> keywords) live with the Phase 1 inventory in [`build-and-project.md`](build-and-project.md);
> the build/publish CI gotchas live with the release pipeline there too.

---

## QUICK REFERENCE — VB.Net gotchas in this codebase

| Pattern | Wrong | Right |
|---|---|---|
| Interface property with Get block | `Property X As String` | `Property X As String Implements IFoo.X` on the Property line |
| Abstract base class | `Class Foo : Implements IFoo` (no members) | `MustInherit Class Foo : MustOverride Function ...() Implements IFoo.X` |
| Enum member = reserved keyword | `Integer`, `Boolean`, `Global`, `Operator`, `Stop`, `Step` | Suffix with Field/Result/etc or rename |
| Variable = VB keyword | `handles`, `step`, `color` | `windowHandles`, `stepAction`, `lineColor` |
| Loop variable shadowing inherited property | `For Each tag In list` inside a Form (Control.Tag exists) | Rename loop variable; produces BC30039 "Loop control variable cannot be a property or a late-bound indexed array." |
| WinForms SelectedIndexChanged on already-matching value | Setting a combo's SelectedIndex to its current value does NOT fire SelectedIndexChanged | When using `SelectedIndexChanged` to drive setup logic during form load, call the handler explicitly after `SelectComboById` (or whatever sets the index). Idempotent and reliable regardless of whether the value actually changed. RuleEditorForm hit this when loading an Instance-scoped rule (default scope == loaded scope == position 0): target combo stayed empty until user toggled scope away and back. |
| **`Me.Invoke` from form constructor before window handle exists** | **`Task.Run(...)` fired from a Form's constructor whose async callee then calls `Me.Invoke(...)` to marshal back** | **WinForms doesn't create the window handle until first Show. Any `Me.Invoke` before that throws `InvalidOperationException` ("Invoke or BeginInvoke cannot be called on a control until the window handle has been created"); a surrounding `Try/Catch` silently eats it and the async work appears to run but no UI updates land. Defer the initial fire-and-forget to `Protected Overrides Sub OnShown(e)` — the handle is guaranteed to exist by then. NewInstallationForm hit this on its install-path-suggestion fetch: form opened with the path field blank, populated only after the user changed game or node (those events fire from a fully-loaded form where the handle does exist). The same shape applies to any async path-or-data-fetch you want firing at form open; constructor-time triggers are a trap.** |
| RootNamespace + explicit Namespace | Double-prefix: GSM.GSM.Plugin | Set RootNamespace to empty string |
| Await in Finally | Not supported in VB.Net | Use ExceptionDispatchInfo pattern OR make the Finally body synchronous |
| Await in Catch | Not supported in VB.Net | Catch-and-rethrow with flag variable |
| Async iterator (Yield in Async) | Not supported in VB.Net | Use callback/Action pattern |
| Async lambda return type | Cannot specify; Task(Of Object) inferred | Extract to named `Private Async Function` |
| Lambda returning interface from concrete | `Function() New ConcreteFoo()` infers Func(Of ConcreteFoo) | `Function() CType(New ConcreteFoo(), IFoo)` for single-expression; `Function() As IFoo ... End Function` for multi-line |
| Single-line Try/Catch | `Try:Catch:End Try` colon-separated | Must be multi-line |
| Integer overflow in hash/accumulator | `h = h * 31 + x.GetHashCode()` (throws OverflowException — VB.Net checks overflow by default, unlike C#) | Use `System.HashCode.Combine(...)`, or cast to Long, or enable RemoveIntegerChecks |
| Null-conditional on LHS | `foo?.Bar = x` | `If foo IsNot Nothing Then foo.Bar = x` |
| Anonymous lambda in Using | Lifetime/disposal issues | Class-level `AddressOf` handlers |
| `proc.WaitForExitAsync` with redirected streams | Deadlocks | Poll `HasExited` instead |
| Extension methods (ILogger) | Not auto-resolved | Add `Imports Microsoft.Extensions.Logging` |
| **StreamReader closes the underlying FileStream** | **`Using fs As New FileStream(...) ... Using reader As New StreamReader(fs) ... End Using ... fs.Length` → ObjectDisposedException** | **StreamReader's End Using disposes the wrapped FileStream by default. Either compute everything you need from `fs` BEFORE the StreamReader's Using opens, or use the `StreamReader(stream, encoding, detectBom, bufferSize, leaveOpen)` overload with `leaveOpen:=True`. Caught when the tailer position cursor's post-read fingerprint compute hit a disposed stream.** |
| Interface implementer missing param-type import | Cascades to BC30401 "cannot implement" + BC30149 "must implement" | Add `Imports` for namespace where parameter types live, even if interface itself is imported. Implementer must resolve every type in the signature, not just the interface name. |
| EF migrations | Not supported in VB.Net | Run from Package Manager Console; use `Add-Migration`/`Update-Database` |
| Comment line inside initializer | Breaks implicit line continuation | Move comment above the initializer |
| Trailing comma before closing brace | Invalid in initializers | Remove trailing commas |
| NETSDK1022 duplicate Compile items | Explicit `<Compile Include>` + SDK auto-discovery | Remove all `<Compile Include>` blocks |
| Content file copy behaviour | `<Content Include="file.json">` | `<None Update="file.json"><CopyToOutputDirectory>PreserveNewest` |
| Regex named captures through string literals | Literal `(?<Name>` | If a tooling issue lowercases names, build via concat: `"(?<" & "Name" & ">..."` |
| Plugin Roslyn compilation excludes Microsoft.VisualBasic | `vbCrLf`, `vbLf`, `vbCr`, `AscW(c)`, `ChrW(n)` in plugin code (BC30451 "not declared") | Define `Private Shared ReadOnly` shims via `Convert.ToChar(...)` at class scope; e.g. `Private Shared ReadOnly _crlf As String = Convert.ToChar(13).ToString() & Convert.ToChar(10).ToString()`. Manager/Node/Contracts code is unaffected — only Roslyn-loaded plugins. |
| **JsonConverter(Of T) in VB** | **Read override takes `ByRef reader As Utf8JsonReader`** | **`Utf8JsonReader` is a ref struct; VB can't consume it. Use `JsonNode` tree traversal instead. BC30668 "Types with embedded references are not supported".** |
| **STJ polymorphism on interfaces** | **`[JsonPolymorphic]` attribute** | **Only works on base classes. Interfaces need hand-rolled polymorphism (see AutomationRuleSerializer).** |
| **EF `Update-Database X` semantics** | **"Undo migration X"** | **"Bring DB to the state after X completed." To undo one migration, name the PREVIOUS one. To undo all, `Update-Database 0`.** |
| **EF migration re-apply after file edit** | **Edit .vb, rebuild, run, expect re-run** | **EF skips migrations already in `__EFMigrationsHistory`. Must rollback THEN reapply, or apply corrective SQL directly.** |
| **EF Core SQLite drops DateTimeKind on read-back** | **Store `DateTime.UtcNow` (Kind=Utc) via EF, read it back, get Kind=Unspecified** | **EF Core's SQLite provider stores DateTime as TEXT in `yyyy-MM-dd HH:mm:ss.fffffff` format — no offset, no Z suffix, so the kind is unrecoverable on read. Downstream `ToString("o")` then emits a no-Z string and any consumer calling `ToUniversalTime()` on the parsed value treats it as Local and shifts by the host's UTC offset. Was a silent filter on chat-mirror cursors after manager restart — every restart-then-chat sequence dropped messages. Fix: tag with `DateTime.SpecifyKind(value, DateTimeKind.Utc)` immediately after EF returns the value, or add a `ValueConverter` on the entity property if it shows up in many places. The column-name suffix (`TimestampUtc` vs `Timestamp`) tells you the contract; restore the metadata to match.** |
| **Roslyn references in self-contained single-file publish** | **Walk `TRUSTED_PLATFORM_ASSEMBLIES` + `MetadataReference.CreateFromFile(path)` — in .NET 6+ single-file mode TPA paths are virtual paths inside the bundle; every `CreateFromFile` throws `FileNotFoundException` and is silently swallowed. Refs end up empty → cascading BC30002 "System.X is not defined" + BC30652 "<Missing Core Assembly>" on every line of every Roslyn-compiled plugin. Only manifests in published builds, not in dev.** | **`Basic.Reference.Assemblies` NuGet (meta-package, NOT the TFM-specific `Basic.Reference.Assemblies.Net80` — only the meta-package exposes the documented `ReferenceAssemblies.Net80` API). For project references you also compile against at runtime (e.g. GSM.Contracts), mark the `<ProjectReference>` with `<ExcludeFromSingleFile>true</ExcludeFromSingleFile>` so they publish as loose DLLs next to the .exe and `Assembly.Location` returns a real path.** |

---

### Reserved keyword landmines (updated)
- `Integer` → `IntegerField` (ConfigFieldType enum)
- `Boolean` → `BooleanField` (ConfigFieldType enum)
- `Global` → `AllInstances` (RuleScope enum)
- `Operator` → `ServerOperator` (CommandPermission enum)
- `Public_` → `Everyone` (CommandPermission enum)
- `Stop` → `stopResult` (variable in RestartInstanceAction)
- `Step` → `stepAction` (variable in SequenceAction)
- `Handles` → `windowHandles` (variable in EnumWindows callbacks)
- `Color` → `lineColor` (variable in LogViewerForm)
- `Tag` → `setTag` (loop variable in RuleEditorForm) — inside Form-derived classes, `tag` resolves to the inherited Control.Tag property; produces BC30039 on `For Each tag In ...`

---

### VB.Net gotchas encountered (additions to the table at the top)

| Pattern | Wrong | Right |
|---|---|---|
| Multi-target boolean DefineConstants | `<DefineConstants>WINDOWS_GUI</DefineConstants>` (ambiguous) | `<DefineConstants>$(DefineConstants),WINDOWS_GUI=True</DefineConstants>` (comma-separated, `=True`) |
| Excluding TFM-specific files from SDK auto-discovery | Conditional `<Compile Include>` blocks | `<ItemGroup Condition="..."><Compile Remove="..."/><None Include="..."/></ItemGroup>` so the SDK still tracks the files but doesn't compile them |
| sc.exe binPath parsing | `sc create svc binPath="path" ...` (no space) | `sc create svc binPath= "path" ...` — the space after `=` is required by sc.exe's tokenizer |
| Process invocation with paths containing quotes | `psi.Arguments = "..."` (manual escaping) | `psi.ArgumentList.Add(...)` — each arg escaped independently, sidesteps the trailing-backslash + escaped-quote class of bug entirely |
| **`ApplicationConfiguration.Initialize()` in VB.Net WinForms** | **Calling it directly — BC30451 "is not declared"** | **The C# WinForms SDK source-generates that helper; the VB.Net SDK does NOT. Call the three calls it would have generated directly: `Application.SetHighDpiMode(HighDpiMode.SystemAware)` → `Application.EnableVisualStyles()` → `Application.SetCompatibleTextRenderingDefault(False)`. Skipping them gives a low-DPI, classic-themed, visually broken form on Windows 10 / 11.** |
| **VB.Net case-insensitivity — parameter shadowing type names** | **`Public Sub Save(path As String) ... Path.GetDirectoryName(path)`** | **VB.Net is case-insensitive, so the `path` parameter shadows `System.IO.Path` and the call resolves as `path.GetDirectoryName(...)` → BC30456 "is not a member of String". Rename the parameter (`filePath`) or fully qualify the type (`Global.System.IO.Path.GetDirectoryName(...)`).** |
| **CA1416 platform-compatibility analyzer doesn't follow indirected guards** | **`If RunningOnWindows() Then [Windows-only API call]` → CA1416 warning even though the function returns `OperatingSystem.IsWindows()`** | **Decorate the wrapper with `<SupportedOSPlatformGuard("windows")>` from `System.Runtime.Versioning`. Now the analyzer treats `If RunningOnWindows() Then ...` as a valid platform guard and Windows-only calls inside the block compile cleanly.** |
| **`AutoSize=True, AutoSizeMode=GrowAndShrink` collapses TableLayoutPanel `Absolute` columns** | **`Absolute=220` column on a `GrowAndShrink` panel — column shrinks to the content's natural width (~90px for a short label) instead of staying at 220** | **Drop `AutoSizeMode=GrowAndShrink`, leave just `AutoSize=True`. The column then honors its absolute width, and content in adjacent columns lines up where you'd expect. Affects any control whose horizontal position you've calculated against the column boundary (sibling button rows, downstream label alignments).** |
| **`System.Text.Json` serializes computed read-only properties** | **`Public ReadOnly Property NeedsAuthTokenSetup As Boolean ... Get ... End Property` — ends up in the JSON output as `"NeedsAuthTokenSetup": false` even though it's a computed-from-other-fields property** | **STJ serializes any public readable property by default. For computed/derived properties that should never appear in the file, decorate with `<JsonIgnore>` from `System.Text.Json.Serialization`. The property remains usable in code but the serializer skips it.** |

---

### WinForms RichTextBox quirks (additions)

**`MessageBeep` on EM_REPLACESEL against ReadOnly.**
`RichTextBox.AppendText`, `SelectedText = "..."`, and the
trim's `Select() + SelectedText = ""` all funnel through
`EM_REPLACESEL`. On a `ReadOnly = True` rich-edit (class
name `RICHEDIT50W`), Windows calls `MessageBeep` BEFORE
performing the replacement — the append still succeeds,
but every call rings the system bell. Rapid programmatic
appends during a high-throughput log burst produce a
continuous ding cascade. Workaround: bracket the
programmatic-mutation block with `_logTextBox.ReadOnly =
False` and restore in Finally. The existing `WM_SETREDRAW
= 0` window across the same span prevents user input from
reaching the control during the toggle, so the brief
`ReadOnly = False` state is invisible.

**`Lines` property is O(N) on read and full-reparse on
write.** `RichTextBox.Lines.Length` walks the entire
control's text and allocates a fresh `String()` array each
call; `RichTextBox.Lines = newArray` re-parses the
assignment as RTF. NEVER read `.Lines.Length` in a hot
path. Track line count and per-line offsets manually — see
`_logLineCount` and the `_logLineEndAbsoluteOffsets` queue
in InstancePanel for the canonical pattern: monotonic
char-written counter, queue of absolute newline offsets,
trim by dequeuing offsets and computing relative cut via
`cutOffset - _logBaseCharOffset`, then `Select(0, relativeCut)`
+ `SelectedText = ""`.

---

### VB.NET gotchas — Phase 5h additions

New rows for the gotcha table:

| Pattern | Wrong | Right |
|---|---|---|
| Imported namespace clashing with same-case-insensitive identifier | `Imports GSM.Plugin` + bare `plugin` variable or named-tuple element `Plugin` in same scope | Use a non-clashing identifier (`gp`, `gamePlugin`); for named tuples that would have a clashing element, use a small private nested class instead |
| Plugin namespace shadows in tuple-element-name position | `List(Of (Plugin As IGamePlugin, ...))` with `Imports GSM.Plugin` active | Replace named tuple with a private nested class with renamed fields, OR use a clearly-different element name (`Game` rather than `Plugin`) |
| Reserved word `node` as parameter name in LINQ context | `Join node In db.Nodes` (compiles, but reads ambiguously next to the `node`/`Node` namespace conventions in WinForms code) | Use `nodeRow` or `nodeEnt` to avoid confusion |

---

### VB.NET / architecture gotchas — connection-binding additions

| Pattern | Wrong | Right |
|---|---|---|
| Per-instance parser correlation state that must outlive the parser | Keep it in a private parser field (lost when the parser is recreated on reconnect, and empty on a fresh Manager process after restart) | Externalise via an opt-in capability interface (`IConnectionBindingAware`) the Manager owns + injects per (re)creation, AND rehydrate from the Node's authoritative `/players` on resync |
| Deduping rows across the Manager↔Node boundary by timestamp | Compare a Manager `DateTime.UtcNow`-stamped row against the Node's `JoinedUtc` with strict `>=` | Don't compare cross-clock timestamps (Manager can lag the Node by seconds); dedup on state — e.g. "an open join already exists" |
| Relying on a single parser leave-signal for History | Assume every disconnect yields a name-resolvable close | LO timeouts emit only `UChannel::Close` (address, no name); resolution depends on a live address→name binding, so that binding must survive reconnect/restart |
| Diffing the Node's `/players` against Manager-tracked players to reconcile missed leaves | Diff against the realm-wide SessionIdentity's open joins, and trust `/players` immediately after a node restart | Scope the open-join query by `InstanceId` (SessionIdentity is realm-wide — would false-leave players on sibling tiles), AND gate on `NodeStatusResponse.UptimeSeconds` (the Node under-reports connected players until log activity re-flows post-restart) |

---

### VB.NET gotchas — Phase 7 additions

New rows for the gotcha table:

| Pattern | Wrong | Right |
|---|---|---|
| **Bare `plugin` identifier inside `GSM.Manager.*` resolves to the sibling namespace `GSM.Plugin`** | **`For Each plugin In ...` (or any `plugin` local) inside `Namespace GSM.Manager.Core` — BC30112 "'GSM.Plugin' is a namespace and cannot be used as an expression" + BC30456** | **Rename the identifier (`utilityPlugin`, `gamePlugin`, `gp`). Note this needs NO `Imports GSM.Plugin`: from inside `GSM.Manager.Core`, name resolution walks up `GSM.Manager.Core` → `GSM.Manager` → `GSM`, and at the `GSM` level `Plugin` is visible as `GSM.Plugin`. The Phase 5h row covers the `Imports`-driven variant; this is the same hazard via sibling-namespace walk. Treat `plugin` as effectively reserved anywhere under `GSM.Manager.*`.** |
| **Case-only platform difference treated as an identity conflict** | **`If rec.Platform <> obs.Platform Then warn` — `"STEAM"` (LO Login-request line) vs `"Steam"` (Persisting line) is the same platform, but `<>` flags it, re-warning every enrich pass (~12s) for as long as the player stays connected** | **Compare stable per-identity STRING attributes case-insensitively: `Not String.Equals(rec.Platform, obs.Platform, StringComparison.OrdinalIgnoreCase)`. Keep first-arrived casing (display attribute, no normalisation churn). NUMERIC ids — PlatformUserId, CharacterId — stay case-SENSITIVE (`Ordinal`): a difference there is a genuine conflict. IdentityResolver.ApplyObservation.** |
| **Async method with a `ByRef` parameter** | **`Private Async Function ProbeAsync(…, ByRef name As String) As Task(Of T)` — BC36926 "Async methods cannot have ByRef parameters" (same family as the Await-in-Catch/Finally limits; the compiler can't carry a ByRef across an await suspension)** | **Return a composite result class carrying every out-value: `Private Class RealmProbeResult : Public Property Verdict : Public Property RealmName : End Class`, `Function ProbeAsync(…) As Task(Of RealmProbeResult)`. lo-myrealm `ProbeRealmPageAsync`. Add a trailing `Return result` after the `Try/Catch` too, or the all-code-paths warning fires.** |
| **Case-insensitive collision between a parameter and a same-named class constant** | **Naming a method parameter `sessionKey` while the class has `Private Const SessionKey` — VB is case-INSENSITIVE, so inside the method `SessionKey` resolves to the PARAMETER, silently shadowing the constant. lo-myrealm's `DiscoverRecordsAsync(sessionKey, …)` called `GetOrCaptureWebSessionAsync(SessionKey, …)` MEANING the constant; when the host's `DiscoverAllPortalRecordsAsync` passed `Nothing` for the parameter, the store received `Nothing` → returned `Nothing` → empty discovery with NO error and NO log. Compiles clean; stayed masked because the diagnostic self-invoke passed the literal key value in, so the parameter happened to equal the constant.** | **Never give a parameter the same name (any casing) as a constant/field you reference unqualified in the body. Rename the parameter (`requestedKey`) so unqualified `SessionKey` binds to the constant — or qualify the member (`LoMyrealmPlugin.SessionKey`). General VB rule: a local/param shadows a same-named member regardless of case; if the body means the member, rename the local or qualify the member. Symptom signature: a value that is correct on one call path and `Nothing` on another, depending on what the caller passed for the shadowing parameter.** |
| **Hosted-tab form using absolute / bottom-anchored layout** | **A form shown borderless inside a `TabControl` (`TopLevel=False` + `FormBorderStyle.None` + `Dock=Fill`) with controls positioned against its nominal `Size` — the tab page's client height is SHORTER than the form thinks, so bottom-anchored controls (button strip, status label, Close) fall off the bottom edge and clip** | **Use DOCKED layout: bottom button strip + status strip `Dock=Bottom`, content `Dock=Fill`, header `Dock=Top`; add Fill FIRST then the docked edges. The layout then adapts to whatever client height the tab grants. `WebSessionsForm` (7-5b) re-learned this; the Phase 6 z-order row is the sibling hazard for the SAME hosted-tab forms.** |
| **Dropping the legacy `' <RequiresContracts: N>' ` comment from a plugin file** | **Shipping a first-party plugin file with ONLY the `<plugin … requiresContracts="N">` manifest attribute. The danger is PLUGIN-SIDE, not Manager-side: 0.3.0 has no auto-update, so a user on a still-0.3.0 Manager can MANUALLY download a newer first-party plugin. PROVEN BEHAVIOUR (tested 2026-06-12 on a v1-contracts build): a plugin file with NO readable version marker is ASSUMED v1 and LOADS — it does NOT refuse. So an attribute-only plugin on an old Manager is silently treated as v1-compatible regardless of the contracts it actually needs; if it relies on newer surface, the version gate that should reject it never fires and it breaks at runtime in whatever way the missing types manifest. This is the worst of the three hypothesised outcomes.** | **First-party plugin files carry BOTH lines INDEFINITELY — a hard safety requirement, not a wart to retire. Absence ≠ rejection; absence = silent v1 assumption, so the legacy line is the ONLY thing preventing a silent mis-load on an old Manager. The line stays until the **v1.0 clean break** (Site's deprecation horizon: legacy-tag support drops at v1.0, when the program is reasonably feature-complete — anything sub-1.0 is dev-quality and a hard break is acceptable there; subject to acceleration only for a deliberate, logical reason). Until that release, the line is mandatory — under manual download a legacy-comment-only Manager can always fetch a newer file. SEPARATELY and additively, a 0.4.0+ Manager may be taught to ALSO read the manifest attribute (attribute supersedes comment when present; legacy-comment reading MUST stay so already-deployed legacy-only plugins keep loading). The two sides are independent: Manager keeps reading the comment (back-compat for existing plugins); files keep writing the comment (forward-safety for old Managers). lo-myrealm + testutility carry both.** |

---

### .NET / hosting gotchas — Phase 8-2 additions

| Pattern | Wrong | Right |
|---|---|---|
| **C#-style discard `_ =` in VB** | **`_ = FireAndForgetAsync()` to swallow a returned Task — `_` is VB's line-continuation token, so `_ = expr` parses as a continuation onto a stray `= expr` → BC30203 "Identifier expected".** | **VB has no `_` discard. Invoke the function as a bare statement (`FireAndForgetAsync()`) when the caller isn't `Async` (no unawaited-call warning fires from a non-Async method), or `Task.Run(Function() FireAndForgetAsync())` for a clean thread-pool fire-and-forget with no `Task(Of Object)` inference. Bit `SelfUpdateService.ScheduleStop`.** |
| **Resolving DI services after `app.Run()` returns** | **`app.Run()` … then `app.Services.GetService(Of T)()`. `HostingAbstractionsHostExtensions.RunAsync` disposes the host (and its `ServiceProvider`) in its `finally`, so any post-`Run` resolve throws `ObjectDisposedException`. A surrounding `Try/Catch` eats it silently, and a flag the service set before shutdown is never read.** | **Capture the service reference (or copy the values you need) BEFORE `app.Run()`. The instance stays alive as long as you hold the reference — only NEW resolutions from the disposed provider throw. This silently regressed the node self-update exit code: the systemd-relaunch flag was set correctly but read off the disposed provider, so the node always exited 0 and systemd never restarted it.** |
