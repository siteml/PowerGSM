Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports System.Text
Imports System.Threading.Tasks
Imports DSharpPlus
Imports DSharpPlus.Entities
Imports DSharpPlus.SlashCommands
Imports Microsoft.EntityFrameworkCore
Imports Microsoft.Extensions.DependencyInjection
Imports Microsoft.Extensions.Logging
Imports GSM.Manager.Data
Imports GSM.Node.Api
Imports GSM.Notification

' ============================================================
'  Phase 5d-4 — slash command surface for the Discord bot.
'
'  Three informational commands. Action-style commands (start/
'  stop/restart) are intentionally not registered here; the
'  Manage button on each panel is the canonical action surface,
'  and routing actions through two parallel UIs would duplicate
'  permission gates and result-rendering with no UX gain.
'
'    /help     — Everyone. Lists the three commands and the
'                panels visible in the current guild. Points
'                users at the Manage button for actions.
'    /panels   — Everyone. Lists the panels in the current
'                guild with channel mentions, useful when an
'                operator forgets where they posted one.
'    /players  — ServerOperator+. Full player list for an
'                instance, with autocomplete on instance names
'                scoped to those visible via panels in the
'                current guild.
'
'  Permission gating uses the same per-guild role mapping cache
'  the Manage flow uses; see DiscordBotPlugin.ResolveUserPermission.
'
'  Per-guild visibility scoping for /players (autocomplete and
'  the command body's eligibility check both go through
'  DiscordBotPlugin.GetInstancesVisibleInGuild) honours the
'  operator's per-guild panel choices: a user with operator
'  privileges in guild A only sees guild A's exposed instances,
'  even if other guilds host more. Per-panel role overrides
'  (the Q5 v2 follow-on) would refine this further; v1 keeps
'  the scoping at the guild level.
' ============================================================

Namespace GSM.Manager.Core

    ' ============================================================
    '  Phase 5d-7a — SlashCommandCatalog
    '
    '  Single declarative source of truth for the slash-command
    '  surface: command name, description, minimum permission
    '  tier, and a one-line "what it sees" note. Three consumers
    '  read from here so they can't drift apart:
    '
    '    1. The <SlashCommand> attributes on GsmSlashCommands —
    '       via the *Name / *Description Const fields below.
    '       DSharpPlus needs compile-time constants for command
    '       registration, so the catalogue can't drive the
    '       attributes at runtime; instead each description lives
    '       in a Const referenced by BOTH the attribute and the
    '       catalogue entry, giving one source with no drift
    '       (Phase 5d-7 Decision #4).
    '    2. /players gating — reads MinimumPermission from here
    '       instead of an inline literal.
    '    3. /help rendering — lists every command from here, so
    '       Discord's /help and (Phase 5d-7c) the Manager's
    '       Commands & Access surface render identical info.
    '
    '  Adding a command (e.g. /lastseen in Phase 5d-8) means:
    '  add its Name/Description Const, add one row to All, and
    '  put the <SlashCommand> attribute on its handler. /help and
    '  the Commands surface then pick it up with no further work.
    ' ============================================================

    Friend NotInheritable Class SlashCommandCatalog

        ' ---- Command name constants (shared with <SlashCommand>) ----
        Friend Const HelpName As String = "help"
        Friend Const PanelsName As String = "panels"
        Friend Const PlayersName As String = "players"
        Friend Const LastSeenName As String = "lastseen"

        ' ---- Description constants (shared with <SlashCommand>) ----
        Friend Const HelpDescription As String = "Show available PowerGSM commands and panels in this server"
        Friend Const PanelsDescription As String = "List PowerGSM panels in this server"
        Friend Const PlayersDescription As String = "Show the player list for an instance"
        Friend Const LastSeenDescription As String = "Show when and where a player was last seen"

        ' Catalogue is a static table; never instantiated.
        Private Sub New()
        End Sub

        ''' <summary>
        ''' One declarative row per slash command.
        ''' </summary>
        Friend NotInheritable Class CommandEntry
            Friend ReadOnly Property Name As String
            Friend ReadOnly Property Description As String
            Friend ReadOnly Property MinimumPermission As CommandPermission
            ''' <summary>One-line "what this command sees / does" note.</summary>
            Friend ReadOnly Property VisibilityNote As String

            Friend Sub New(name As String,
                           description As String,
                           minimumPermission As CommandPermission,
                           visibilityNote As String)
                Me.Name = name
                Me.Description = description
                Me.MinimumPermission = minimumPermission
                Me.VisibilityNote = visibilityNote
            End Sub
        End Class

        ''' <summary>
        ''' Every registered slash command, in display order.
        ''' </summary>
        Friend Shared ReadOnly All As IReadOnlyList(Of CommandEntry) = New List(Of CommandEntry) From {
            New CommandEntry(HelpName, HelpDescription, CommandPermission.Everyone,
                             "Lists the commands and the panels configured in this server."),
            New CommandEntry(PanelsName, PanelsDescription, CommandPermission.Everyone,
                             "Lists this server's panels and their channels; nothing instance-specific."),
            New CommandEntry(PlayersName, PlayersDescription, CommandPermission.ServerOperator,
                             "Reads the live player list for one instance visible in this server's panels."),
            New CommandEntry(LastSeenName, LastSeenDescription, CommandPermission.ServerOperator,
                             "Looks up a player's most recent join/leave across instances visible in this server.")
        }

        ''' <summary>
        ''' Look up a command entry by name (case-insensitive).
        ''' Returns Nothing if no such command is catalogued.
        ''' </summary>
        Friend Shared Function Find(commandName As String) As CommandEntry
            Return All.FirstOrDefault(
                Function(c) String.Equals(c.Name, commandName, StringComparison.OrdinalIgnoreCase))
        End Function

    End Class

    Public Class GsmSlashCommands
        Inherits ApplicationCommandModule

        ' DSharpPlus.SlashCommands instantiates module classes
        ' afresh per interaction. Using a parameterless constructor
        ' and pulling services off InteractionContext.Services
        ' keeps DI failures out of the construction path: a
        ' parameterised constructor that DI can't satisfy
        ' hard-fails the whole interaction with no surface for
        ' the user, while reading services via ctx.Services lets
        ' us return a graceful ephemeral if a dependency is
        ' missing (e.g. plugin reload mid-interaction).
        Public Sub New()
        End Sub

        ' ============================================================
        '  /help
        ' ============================================================

        <SlashCommand(SlashCommandCatalog.HelpName, SlashCommandCatalog.HelpDescription)>
        Public Async Function HelpAsync(ctx As InteractionContext) As Task
            Dim sb As New StringBuilder()
            sb.AppendLine("## PowerGSM commands")
            ' Rendered from the SlashCommandCatalog so Discord's /help
            ' and the Manager's Commands surface stay in lockstep.
            For Each cmd In SlashCommandCatalog.All
                sb.AppendLine($"• `/{cmd.Name}` — {cmd.Description}{PermissionTag(cmd.MinimumPermission)}")
            Next
            sb.AppendLine()
            sb.AppendLine("To start, stop, or restart instances, click **Manage** on a panel above.")

            Dim panelLines = Await BuildPanelLinesAsync(ctx)
            If panelLines IsNot Nothing AndAlso panelLines.Count > 0 Then
                sb.AppendLine()
                sb.AppendLine("**Panels in this server:**")
                For Each line In panelLines
                    sb.AppendLine($"• {line}")
                Next
            End If

            Await ReplyEphemeralAsync(ctx, sb.ToString())
        End Function

        ' ============================================================
        '  /panels
        ' ============================================================

        <SlashCommand(SlashCommandCatalog.PanelsName, SlashCommandCatalog.PanelsDescription)>
        Public Async Function PanelsAsync(ctx As InteractionContext) As Task
            Dim panelLines = Await BuildPanelLinesAsync(ctx)
            Dim sb As New StringBuilder()
            sb.AppendLine("## Panels in this server")
            If panelLines Is Nothing OrElse panelLines.Count = 0 Then
                sb.AppendLine("_No panels configured here yet._")
            Else
                For Each line In panelLines
                    sb.AppendLine($"• {line}")
                Next
            End If

            Await ReplyEphemeralAsync(ctx, sb.ToString())
        End Function

        ' ============================================================
        '  /players <instance>
        ' ============================================================

        <SlashCommand(SlashCommandCatalog.PlayersName, SlashCommandCatalog.PlayersDescription)>
        Public Async Function PlayersAsync(
                ctx As InteractionContext,
                <[Option]("instance", "Which instance"),
                 Autocomplete(GetType(InstanceAutocompleteProvider))>
                instanceId As String) As Task

            ' DM context has no permission scope and no guild-
            ' specific panels — refuse outright rather than
            ' degrading to "permission denied" which would be
            ' confusing.
            If ctx.Guild Is Nothing OrElse ctx.Member Is Nothing Then
                Await ReplyEphemeralAsync(ctx, "This command only works inside a server.")
                Return
            End If

            Dim botPlugin = ctx.Services.GetService(Of DiscordBotPlugin)()
            If botPlugin Is Nothing Then
                Await ReplyEphemeralAsync(ctx, "Discord bot plugin unavailable.")
                Return
            End If

            Dim guildIdStr = ctx.Guild.Id.ToString()
            ' Minimum tier comes from the catalogue (single source
            ' of truth) rather than an inline literal, so the gate
            ' here and the tier shown in /help / the Commands surface
            ' can't diverge.
            Dim playersCmd = SlashCommandCatalog.Find(SlashCommandCatalog.PlayersName)
            Dim perm = botPlugin.ResolveUserPermission(ctx.Member, guildIdStr)
            If perm < playersCmd.MinimumPermission Then
                Await ReplyEphemeralAsync(ctx,
                    $"You need a role mapped to {playersCmd.MinimumPermission} (or higher) to run this command.")
                Return
            End If

            ' Re-verify the picked instance is actually visible
            ' in this guild's panel scope. Discord's autocomplete
            ' suggests matches but doesn't force the user to pick
            ' from them — they can type any string into the
            ' option and submit. The guild-scoping check here
            ' enforces what autocomplete only suggests.
            Dim visible = botPlugin.GetInstancesVisibleInGuild(guildIdStr)
            Dim entry = visible.FirstOrDefault(Function(x) String.Equals(x.InstanceId, instanceId, StringComparison.OrdinalIgnoreCase))
            If entry Is Nothing Then
                Await ReplyEphemeralAsync(ctx,
                    "That instance isn't visible in any panel configured for this server.")
                Return
            End If

            ' Defer — node fetch can take a beat for instances
            ' on busy servers.
            Try
                Await ctx.DeferAsync(ephemeral:=True)
            Catch ex As Exception
                Dim logger = TryGetLogger(ctx)
                If logger IsNot Nothing Then
                    logger.LogWarning(ex, "/players defer failed for {Instance}", instanceId)
                End If
                Return
            End Try

            Dim im = ctx.Services.GetService(Of InstanceManager)()
            If im Is Nothing Then
                Await EditResponseAsync(ctx, "Instance manager unavailable.")
                Return
            End If

            Dim players As IReadOnlyList(Of PlayerSession) = Nothing
            ' VB.Net forbids Await inside a Catch (BC36943).
            ' Capture any error message and emit the response
            ' after the Try block instead.
            Dim fetchError As String = Nothing
            Try
                players = Await im.GetPlayersAsync(entry.InstanceId)
            Catch ex As Exception
                fetchError = ex.Message
            End Try
            If fetchError IsNot Nothing Then
                Await EditResponseAsync(ctx, $"Failed to fetch players: {fetchError}")
                Return
            End If

            ' Phase 5g-2d Round 3c — enrich against the resolver so
            ' the /players list shows resolved character names even
            ' when this Node snapshot is missing them.
            players = im.EnrichPlayers(entry.InstanceId, players)

            Await EditResponseAsync(ctx, BuildPlayersResponse(entry, players))
        End Function

        ' ============================================================
        '  /lastseen <player>
        ' ============================================================

        <SlashCommand(SlashCommandCatalog.LastSeenName, SlashCommandCatalog.LastSeenDescription)>
        Public Async Function LastSeenAsync(
                ctx As InteractionContext,
                <[Option]("player", "Player name — Steam handle or in-game character"),
                 Autocomplete(GetType(LastSeenPlayerAutocompleteProvider))>
                Optional player As String = Nothing,
                <[Option]("instance", "Limit to one instance"),
                 Autocomplete(GetType(InstanceAutocompleteProvider))>
                Optional instanceScope As String = Nothing,
                <[Option]("game", "Limit to one game"),
                 Autocomplete(GetType(GameAutocompleteProvider))>
                Optional gameScope As String = Nothing,
                <[Option]("installation", "Limit to one installation"),
                 Autocomplete(GetType(InstallationAutocompleteProvider))>
                Optional installScope As String = Nothing) As Task

            ' DM context has no guild scope — refuse cleanly, same as
            ' /players.
            If ctx.Guild Is Nothing OrElse ctx.Member Is Nothing Then
                Await ReplyEphemeralAsync(ctx, "This command only works inside a server.")
                Return
            End If

            Dim botPlugin = ctx.Services.GetService(Of DiscordBotPlugin)()
            If botPlugin Is Nothing Then
                Await ReplyEphemeralAsync(ctx, "Discord bot plugin unavailable.")
                Return
            End If

            Dim guildIdStr = ctx.Guild.Id.ToString()
            ' Minimum tier from the catalogue (single source of truth),
            ' matching /players.
            Dim cmd = SlashCommandCatalog.Find(SlashCommandCatalog.LastSeenName)
            Dim perm = botPlugin.ResolveUserPermission(ctx.Member, guildIdStr)
            If perm < cmd.MinimumPermission Then
                Await ReplyEphemeralAsync(ctx,
                    $"You need a role mapped to {cmd.MinimumPermission} (or higher) to run this command.")
                Return
            End If

            ' Need at least a player or a scope; scope filters are
            ' mutually exclusive.
            Dim scopeCount = 0
            If Not String.IsNullOrWhiteSpace(instanceScope) Then scopeCount += 1
            If Not String.IsNullOrWhiteSpace(gameScope) Then scopeCount += 1
            If Not String.IsNullOrWhiteSpace(installScope) Then scopeCount += 1
            If scopeCount > 1 Then
                Await ReplyEphemeralAsync(ctx,
                    "Pick just one scope to filter by — instance, game, or installation.")
                Return
            End If
            If String.IsNullOrWhiteSpace(player) AndAlso scopeCount = 0 Then
                Await ReplyEphemeralAsync(ctx,
                    "Give me a player to look up, or a scope (instance / game / installation) to list who's been seen.")
                Return
            End If

            ' Defer — the history query runs on a thread-pool hop.
            Try
                Await ctx.DeferAsync(ephemeral:=True)
            Catch ex As Exception
                Dim logger = TryGetLogger(ctx)
                If logger IsNot Nothing Then
                    logger.LogWarning(ex, "/lastseen defer failed for {Player}", player)
                End If
                Return
            End Try

            Dim history = ctx.Services.GetService(Of HistoryQueryService)()
            If history Is Nothing Then
                Await EditResponseAsync(ctx, "History service unavailable.")
                Return
            End If

            ' Only report players seen on instances exposed via this
            ' server's panels — same scoping as /players. Autocomplete
            ' suggests; this set enforces.
            Dim visibleIds As New HashSet(Of String)(
                botPlugin.GetInstancesVisibleInGuild(guildIdStr).
                    Select(Function(x) x.InstanceId),
                StringComparer.OrdinalIgnoreCase)
            If visibleIds.Count = 0 Then
                Await EditResponseAsync(ctx, "No instances are visible in this server's panels.")
                Return
            End If

            ' Optional scope filter (instance / game / installation —
            ' mutual exclusivity was validated before the defer). Narrow
            ' the visible set to the chosen scope; for game/installation
            ' that resolves to an instance set via the Instances table,
            ' intersected with what's visible here. Autocomplete suggests
            ' scope values; this is where they're enforced.
            Dim scopeIds As HashSet(Of String) = visibleIds
            Dim scopeLabel As String = Nothing
            If Not String.IsNullOrWhiteSpace(instanceScope) Then
                scopeLabel = "the selected instance"
                scopeIds = New HashSet(Of String)(
                    visibleIds.Where(Function(id) String.Equals(
                        id, instanceScope.Trim(), StringComparison.OrdinalIgnoreCase)),
                    StringComparer.OrdinalIgnoreCase)
            ElseIf Not String.IsNullOrWhiteSpace(gameScope) OrElse
                   Not String.IsNullOrWhiteSpace(installScope) Then
                Dim matchIds As List(Of String) = Nothing
                Dim scopeError As String = Nothing
                Try
                    Using dbScope = ctx.Services.CreateScope()
                        Dim db = dbScope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                        Dim q = db.Instances.AsQueryable()
                        If Not String.IsNullOrWhiteSpace(gameScope) Then
                            Dim g = gameScope.Trim()
                            scopeLabel = $"game **{EscapeForDiscord(g)}**"
                            q = q.Where(Function(i) i.GameId = g)
                        Else
                            Dim inst = installScope.Trim()
                            scopeLabel = "the selected installation"
                            q = q.Where(Function(i) i.InstallationId = inst)
                        End If
                        matchIds = q.Select(Function(i) i.InstanceId).ToList()
                    End Using
                Catch ex As Exception
                    scopeError = "Couldn't resolve that scope filter."
                End Try
                If scopeError IsNot Nothing Then
                    Await EditResponseAsync(ctx, scopeError)
                    Return
                End If
                scopeIds = New HashSet(Of String)(
                    matchIds.Where(Function(id) visibleIds.Contains(id)),
                    StringComparer.OrdinalIgnoreCase)
            End If
            If scopeIds.Count = 0 Then
                Await EditResponseAsync(ctx,
                    $"Nothing visible in {If(scopeLabel, "that scope")} in this server.")
                Return
            End If

            ' === Roster mode (scope-only, no player) ===
            ' List the most-recently-seen players in the scope. No
            ' PlayerNamePattern — fetch all join/leave activity (newest
            ' first), keep what's in scope, and let BuildRosterResponse
            ' dedup to one line per player.
            If String.IsNullOrWhiteSpace(player) Then
                Dim rosterFilter As New HistoryFilter With {
                    .StartUtc = DateTime.MinValue,
                    .EndUtc = DateTime.UtcNow,
                    .IncludeChat = False,
                    .IncludeJoins = True,
                    .IncludeLeaves = True
                }
                Dim rosterErr As String = Nothing
                Dim rosterResult As TimelineResult = Nothing
                Try
                    rosterResult = Await history.QueryTimelineAsync(
                        rosterFilter, System.Threading.CancellationToken.None)
                Catch ex As Exception
                    rosterErr = ex.Message
                End Try
                If rosterErr IsNot Nothing Then
                    Await EditResponseAsync(ctx, $"Failed to build roster: {rosterErr}")
                    Return
                End If
                Dim rosterRows As New List(Of TimelineRow)
                If rosterResult IsNot Nothing AndAlso rosterResult.Rows IsNot Nothing Then
                    For Each r In rosterResult.Rows
                        If Not String.IsNullOrEmpty(r.InstanceId) AndAlso
                           scopeIds.Contains(r.InstanceId) Then
                            rosterRows.Add(r)
                        End If
                    Next
                End If
                Await EditResponseAsync(ctx, BuildRosterResponse(rosterRows, scopeLabel))
                Return
            End If

            ' Identity-aware lookup: the typed string may be an in-game
            ' character name, but PlayerActivity.PlayerName stores the
            ' platform persona (Steam handle on LO, etc.). Resolve the
            ' typed name to an identity via the resolver and search by
            ' its persona, so a character-name query finds the same
            ' history a persona query would. Falls back to the literal
            ' typed string when nothing resolves.
            Dim resolver = ctx.Services.GetService(Of IdentityResolver)()
            Dim resolvedRec = ResolveTypedIdentity(resolver, player.Trim())
            Dim pattern = player.Trim()
            If resolvedRec IsNot Nothing Then
                Dim canonical = If(Not String.IsNullOrEmpty(resolvedRec.PlatformPersona),
                                   resolvedRec.PlatformPersona, resolvedRec.DisplayName)
                If Not String.IsNullOrEmpty(canonical) Then pattern = canonical
            End If

            ' Join/leave history only (no chat), newest-first, across
            ' all sessions; rows are filtered to this guild's visible
            ' instances below. StartUtc = MinValue spans all history
            ' (PlayerActivity is never time-pruned).
            Dim filter As New HistoryFilter With {
                .StartUtc = DateTime.MinValue,
                .EndUtc = DateTime.UtcNow,
                .PlayerNamePattern = pattern,
                .IncludeChat = False,
                .IncludeJoins = True,
                .IncludeLeaves = True
            }

            Dim fetchError As String = Nothing
            Dim result As TimelineResult = Nothing
            Try
                result = Await history.QueryTimelineAsync(filter, System.Threading.CancellationToken.None)
            Catch ex As Exception
                fetchError = ex.Message
            End Try
            If fetchError IsNot Nothing Then
                Await EditResponseAsync(ctx, $"Failed to look up history: {fetchError}")
                Return
            End If

            Dim allRows As IReadOnlyList(Of TimelineRow) =
                If(result IsNot Nothing AndAlso result.Rows IsNot Nothing,
                   result.Rows, New List(Of TimelineRow)())

            ' Keep only rows on in-scope instances (the guild-visible
            ' set, already narrowed by any scope filter above),
            ' preserving the newest-first order the service returned.
            Dim visibleRows As New List(Of TimelineRow)
            For Each r In allRows
                If Not String.IsNullOrEmpty(r.InstanceId) AndAlso
                   scopeIds.Contains(r.InstanceId) Then
                    visibleRows.Add(r)
                End If
            Next

            If visibleRows.Count = 0 Then
                Dim scopeSuffix = If(scopeLabel IsNot Nothing, $" in {scopeLabel}", "")
                Await EditResponseAsync(ctx,
                    $"No record of **{EscapeForDiscord(player.Trim())}**{scopeSuffix} on any instance visible in this server.")
                Return
            End If

            Await EditResponseAsync(ctx, BuildLastSeenResponse(visibleRows, resolvedRec))
        End Function

        ''' <summary>
        ''' Render the /lastseen answer from the newest visible
        ''' activity row. Reuses HistoryQueryService's SourceLabel
        ''' verbatim, so /lastseen and the History grid show the
        ''' identical "where" string. Rows arrive newest-first.
        '''
        ''' The "also matched" disambiguation groups by IDENTITY, not
        ''' display string: a single player whose rows render under
        ''' both a resolved character name and the raw persona (older
        ''' rows the resolver hadn't bound yet) collapses to one, so a
        ''' player never lists itself. resolvedRec (when the typed name
        ''' resolved to an identity) supplies that identity's facets;
        ''' otherwise the top row's own facets anchor it.
        ''' </summary>
        Private Shared Function BuildLastSeenResponse(
                rows As IReadOnlyList(Of TimelineRow),
                resolvedRec As IdentityRecord) As String

            Dim top = rows(0)
            Dim sb As New StringBuilder()

            Dim character = If(Not String.IsNullOrEmpty(top.CharacterName),
                               top.CharacterName, top.PlayerName)
            Dim persona = top.PlatformPersona
            Dim personaDistinct = Not String.IsNullOrEmpty(persona) AndAlso
                                  Not String.Equals(persona, character, StringComparison.Ordinal)

            Dim namePart As String = $"**{EscapeForDiscord(character)}**"
            If personaDistinct Then namePart &= $" ({EscapeForDiscord(persona)})"

            ' The newest event's kind is the current-presence signal:
            ' a join with no later leave = still on (active now); a
            ' leave = currently off. Surfacing "active now / offline"
            ' rather than the raw "joined / left" verb avoids the
            ' misleading "last seen … (joined)" read for someone who's
            ' actually still connected.
            Dim isOnline = (top.Kind = TimelineRow.RowKind.Join)

            ' TimestampUtc is UTC; SpecifyKind before the zero-offset
            ' DateTimeOffset so <t:R> renders correctly for any viewer.
            Dim unix = New DateTimeOffset(
                DateTime.SpecifyKind(top.TimestampUtc, DateTimeKind.Utc),
                TimeSpan.Zero).ToUnixTimeSeconds()

            Dim where = If(Not String.IsNullOrEmpty(top.SourceLabel),
                           top.SourceLabel, "(unknown location)")

            If isOnline Then
                sb.AppendLine($"{namePart} is **active now** — joined <t:{unix}:R>")
            Else
                sb.AppendLine($"{namePart} is **offline** — last seen <t:{unix}:R>")
            End If
            sb.AppendLine($"• {EscapeForDiscord(where)}")

            ' Collect the target identity's facets so ALL of its rows
            ' (resolved character name OR raw persona) are excluded from
            ' "also matched" — the player never lists itself.
            Dim idIds As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            Dim idNames As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase) From {character}
            If Not String.IsNullOrEmpty(top.PlatformUserId) Then idIds.Add(top.PlatformUserId)
            If Not String.IsNullOrEmpty(top.CharacterId) Then idIds.Add(top.CharacterId)
            If Not String.IsNullOrEmpty(persona) Then idNames.Add(persona)
            If resolvedRec IsNot Nothing Then
                For Each i In {resolvedRec.PlatformUserId, resolvedRec.CharacterId}
                    If Not String.IsNullOrEmpty(i) Then idIds.Add(i)
                Next
                For Each s In {resolvedRec.PlatformPersona, resolvedRec.DisplayName}
                    If Not String.IsNullOrEmpty(s) Then idNames.Add(s)
                Next
            End If

            ' Genuinely-other players the search also matched.
            Dim others As New List(Of String)
            Dim seen As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            For Each r In rows
                If (Not String.IsNullOrEmpty(r.PlatformUserId) AndAlso idIds.Contains(r.PlatformUserId)) OrElse
                   (Not String.IsNullOrEmpty(r.CharacterId) AndAlso idIds.Contains(r.CharacterId)) Then
                    Continue For
                End If
                Dim nm = If(Not String.IsNullOrEmpty(r.CharacterName), r.CharacterName, r.PlayerName)
                If String.IsNullOrEmpty(nm) OrElse idNames.Contains(nm) Then Continue For
                If seen.Add(nm) Then
                    others.Add(nm)
                    If others.Count >= 8 Then Exit For
                End If
            Next
            If others.Count > 0 Then
                sb.AppendLine()
                sb.AppendLine($"_Also matched: {String.Join(", ", others.Select(AddressOf EscapeForDiscord))} — narrow the name to pick one._")
            End If

            Return sb.ToString()
        End Function

        ''' <summary>
        ''' Roster mode: the most-recently-seen players in the chosen
        ''' scope, one line each (name + relative last-seen + kind),
        ''' newest-first, deduplicated by identity (name or id already
        ''' seen → skip). Caps both the count (~20) and total length to
        ''' stay under Discord's message limit.
        ''' </summary>
        Private Shared Function BuildRosterResponse(
                rows As IReadOnlyList(Of TimelineRow),
                scopeLabel As String) As String
            Dim sb As New StringBuilder()
            Dim header = If(scopeLabel IsNot Nothing,
                            $"Recently seen in {scopeLabel}", "Recently seen")
            sb.AppendLine($"## {header}")

            If rows Is Nothing OrElse rows.Count = 0 Then
                sb.Append("_No players seen here yet._")
                Return sb.ToString()
            End If

            Dim seenNames As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            Dim seenIds As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            Dim shown = 0
            For Each r In rows   ' newest-first
                Dim nm = If(Not String.IsNullOrEmpty(r.CharacterName), r.CharacterName, r.PlayerName)
                If String.IsNullOrEmpty(nm) Then Continue For
                ' Dedup by identity: skip if this name or any id was seen.
                If seenNames.Contains(nm) OrElse
                   (Not String.IsNullOrEmpty(r.PlatformUserId) AndAlso seenIds.Contains(r.PlatformUserId)) OrElse
                   (Not String.IsNullOrEmpty(r.CharacterId) AndAlso seenIds.Contains(r.CharacterId)) Then
                    Continue For
                End If
                seenNames.Add(nm)
                If Not String.IsNullOrEmpty(r.PlatformUserId) Then seenIds.Add(r.PlatformUserId)
                If Not String.IsNullOrEmpty(r.CharacterId) Then seenIds.Add(r.CharacterId)

                ' Newest event per identity → current presence (see
                ' BuildLastSeenResponse): join = active now, leave =
                ' offline, instead of the raw joined/left verb.
                Dim isOnline = (r.Kind = TimelineRow.RowKind.Join)
                Dim unix = New DateTimeOffset(
                    DateTime.SpecifyKind(r.TimestampUtc, DateTimeKind.Utc),
                    TimeSpan.Zero).ToUnixTimeSeconds()
                Dim persona = r.PlatformPersona
                Dim personaDistinct = Not String.IsNullOrEmpty(persona) AndAlso
                                      Not String.Equals(persona, nm, StringComparison.Ordinal)
                Dim line As String = $"• **{EscapeForDiscord(nm)}**"
                If personaDistinct Then line &= $" ({EscapeForDiscord(persona)})"
                line &= If(isOnline,
                           $" — active now (since <t:{unix}:R>)",
                           $" — offline (last seen <t:{unix}:R>)")

                If sb.Length + line.Length + 32 > 1800 Then
                    sb.AppendLine("_…and more_")
                    Exit For
                End If
                sb.AppendLine(line)
                shown += 1
                If shown >= 20 Then Exit For
            Next
            If shown = 0 Then sb.Append("_No players seen here yet._")
            Return sb.ToString()
        End Function

        ''' <summary>
        ''' Resolve a typed name to its best-guess identity by scanning
        ''' the resolver's records for an EXACT (case-insensitive) match
        ''' on any facet — persona, display name, character id, or
        ''' platform user id. Exact-only avoids a substring collision
        ''' hijacking the search; an unmatched name falls through to the
        ''' raw substring query. Newest-observed wins when several match.
        ''' Returns Nothing when there's no resolver or no exact hit.
        ''' </summary>
        Private Shared Function ResolveTypedIdentity(
                resolver As IdentityResolver, typed As String) As IdentityRecord
            If resolver Is Nothing OrElse String.IsNullOrWhiteSpace(typed) Then Return Nothing
            Try
                Dim hit As IdentityRecord = Nothing
                For Each r In resolver.GetAllRecords()
                    If IdentityFacetEquals(r, typed) Then
                        If hit Is Nothing OrElse r.LastObservedUtc > hit.LastObservedUtc Then
                            hit = r
                        End If
                    End If
                Next
                Return hit
            Catch
                Return Nothing
            End Try
        End Function

        ''' <summary>
        ''' True when any identity facet of the record equals the typed
        ''' string (case-insensitive).
        ''' </summary>
        Private Shared Function IdentityFacetEquals(
                r As IdentityRecord, typed As String) As Boolean
            For Each f In {r.PlatformPersona, r.DisplayName, r.PlatformUserId, r.CharacterId}
                If Not String.IsNullOrEmpty(f) AndAlso
                   String.Equals(f, typed, StringComparison.OrdinalIgnoreCase) Then
                    Return True
                End If
            Next
            Return False
        End Function

        ' ============================================================
        '  Helpers
        ' ============================================================

        ''' <summary>
        ''' Build the per-guild panel summary used by both /help
        ''' and /panels. Returns one line per panel: bold name +
        ''' channel mention. The mention syntax (&lt;#NNN&gt;) renders
        ''' as a clickable channel link in Discord, so users can
        ''' jump to a panel without scrolling.
        ''' </summary>
        Private Shared Async Function BuildPanelLinesAsync(
                ctx As InteractionContext) As Task(Of List(Of String))
            Dim lines As New List(Of String)
            If ctx.Guild Is Nothing Then Return lines

            Try
                Using scope = ctx.Services.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    Dim guildIdStr = ctx.Guild.Id.ToString()
                    Dim panels = Await db.DiscordPanels.
                        Where(Function(p) p.GuildId = guildIdStr).
                        OrderBy(Function(p) p.DisplayName).
                        ToListAsync()
                    For Each p In panels
                        Dim name = If(String.IsNullOrEmpty(p.DisplayName), "(unnamed)", p.DisplayName)
                        Dim channelMention As String
                        If Not String.IsNullOrEmpty(p.ChannelId) Then
                            channelMention = $"<#{p.ChannelId}>"
                        Else
                            channelMention = "_(no channel)_"
                        End If
                        lines.Add($"**{EscapeForDiscord(name)}** in {channelMention}")
                    Next
                End Using
            Catch
                ' Best-effort. An empty list still gives /help a
                ' useful response, and /panels degrades to "none
                ' configured" which is no worse than what the
                ' user would see if the table really were empty.
            End Try
            Return lines
        End Function

        ''' <summary>
        ''' Render the /players response. Lifted out of the
        ''' command body so the Discord-specific Markdown stays
        ''' in one place. Truncates at 1800 chars so we stay
        ''' under Discord's 2000-char message-content cap with
        ''' headroom for the truncation marker.
        ''' </summary>
        Private Shared Function BuildPlayersResponse(
                entry As InstanceLookupEntry,
                players As IReadOnlyList(Of PlayerSession)) As String
            Dim sb As New StringBuilder()
            sb.AppendLine($"## Players: {EscapeForDiscord(entry.DisplayName)}")
            If players Is Nothing OrElse players.Count = 0 Then
                sb.Append("_No players online._")
                Return sb.ToString()
            End If
            sb.AppendLine($"{players.Count} player(s):")

            Dim shown = 0
            For Each p In players
                ' IdentityFormatter coalesces DisplayName →
                ' PlatformPersona → fallback so this rendering
                ' agrees with the History window's verdict for the
                ' same player — they call into the same helper.
                ' On Last Oasis the chosen in-game character name
                ' lands in DisplayName (resolved by the Node's
                ' identity chain from the Persisting / chat / cached
                ' lookup paths) and the Steam handle in
                ' PlatformPersona; PlatformPersona is the fallback
                ' for the short window after a player joins but
                ' before the Node has bridged their PlatformUserId
                ' to a DisplayName. On Factorio only DisplayName is
                ' populated. "(unknown)" only renders when both
                ' identity surfaces are empty, which would indicate
                ' the Node's identity resolver hadn't yet bound
                ' either — worth showing rather than silently
                ' dropping the row.
                Dim displayed = IdentityFormatter.Format(p.DisplayName, p.PlatformPersona, "(unknown)")
                Dim line As New StringBuilder()
                line.Append($"• **{EscapeForDiscord(displayed)}**")
                ' Phase 5d-2 — identity format:
                '   character (Platform: persona)  when the displayed
                '     name is a distinct character AND we also have the
                '     platform persona — surfaces both so an operator
                '     can tie the in-game name to the account handle.
                '   persona (Platform)             when displayed already
                '     IS the persona (no distinct character known) —
                '     repeating it after the colon would be noise.
                '   bare (Platform) / (persona)    edge cases when only
                '     one of the two is available.
                Dim hasPlatform = Not String.IsNullOrEmpty(p.Platform)
                Dim personaDistinct = Not String.IsNullOrEmpty(p.PlatformPersona) AndAlso
                                      Not String.Equals(p.PlatformPersona, displayed, StringComparison.Ordinal)
                If hasPlatform AndAlso personaDistinct Then
                    line.Append($" ({EscapeForDiscord(p.Platform)}: {EscapeForDiscord(p.PlatformPersona)})")
                ElseIf hasPlatform Then
                    line.Append($" ({EscapeForDiscord(p.Platform)})")
                ElseIf personaDistinct Then
                    line.Append($" ({EscapeForDiscord(p.PlatformPersona)})")
                End If
                If p.JoinedUtc <> DateTime.MinValue Then
                    ' JoinedUtc on the wire is UTC. SpecifyKind
                    ' makes that explicit before constructing a
                    ' DateTimeOffset with TimeSpan.Zero — the
                    ' single-arg DateTimeOffset constructor uses
                    ' the local UTC offset, which would mis-render
                    ' for any non-UTC viewer.
                    Dim unix = New DateTimeOffset(
                        DateTime.SpecifyKind(p.JoinedUtc, DateTimeKind.Utc),
                        TimeSpan.Zero).ToUnixTimeSeconds()
                    line.Append($" — joined <t:{unix}:R>")
                End If

                ' Cap before append: stop adding rows when the
                ' truncation marker would push us over 2000.
                Dim candidate = line.ToString()
                If sb.Length + candidate.Length + 32 > 1800 Then
                    sb.AppendLine($"_…and {players.Count - shown} more_")
                    Exit For
                End If
                sb.AppendLine(candidate)
                shown += 1
            Next
            Return sb.ToString()
        End Function

        ''' <summary>
        ''' Friendly parenthetical for /help describing the minimum
        ''' tier — empty for Everyone-tier commands. Reads the tier
        ''' from the SlashCommandCatalog entry so the annotation
        ''' can't drift from what's actually enforced.
        ''' </summary>
        Private Shared Function PermissionTag(perm As CommandPermission) As String
            Select Case perm
                Case CommandPermission.ServerOperator
                    Return " (operators only)"
                Case CommandPermission.Administrator
                    Return " (admins only)"
                Case Else
                    Return ""
            End Select
        End Function

        Private Shared Async Function ReplyEphemeralAsync(
                ctx As InteractionContext, content As String) As Task
            Try
                Await ctx.CreateResponseAsync(
                    InteractionResponseType.ChannelMessageWithSource,
                    New DiscordInteractionResponseBuilder().
                        WithContent(content).
                        AsEphemeral(True))
            Catch
                ' Interaction may have already been responded to
                ' or timed out — nothing useful to do here.
            End Try
        End Function

        Private Shared Async Function EditResponseAsync(
                ctx As InteractionContext, content As String) As Task
            Try
                Dim wb As New DiscordWebhookBuilder()
                wb.WithContent(content)
                Await ctx.EditResponseAsync(wb)
            Catch
            End Try
        End Function

        Private Shared Function TryGetLogger(ctx As InteractionContext) As ILogger
            Try
                Return ctx.Services.GetService(Of ILogger(Of GsmSlashCommands))()
            Catch
                Return Nothing
            End Try
        End Function

        ''' <summary>
        ''' Minimal Markdown escape — same rules as
        ''' DiscordBotPlugin.Escape (kept duplicated rather than
        ''' factored to a shared helper because the two classes
        ''' live in slightly different concerns; the rules are
        ''' stable enough that drift is unlikely).
        ''' </summary>
        Private Shared Function EscapeForDiscord(s As String) As String
            If String.IsNullOrEmpty(s) Then Return ""
            Return s.Replace("\", "\\").
                     Replace("*", "\*").
                     Replace("_", "\_").
                     Replace("`", "\`")
        End Function

    End Class

    ' ============================================================
    '  Autocomplete provider for /players' instance argument.
    '
    '  Returns instances visible via any panel in the current
    '  guild, filtered by the partial typed string. Discord
    '  fires this on each keystroke (with a small debounce on
    '  their side); we cap at 25 results since that's the
    '  Discord limit on autocomplete choices.
    '
    '  Failures are silent: an exception in the provider returns
    '  no suggestions, so the user falls through to typing the
    '  ID manually. The command body re-validates so a typed
    '  ID still goes through the per-guild visibility check.
    ' ============================================================

    Public Class InstanceAutocompleteProvider
        Implements IAutocompleteProvider

        Public Function Provider(ctx As AutocompleteContext) _
                As Task(Of IEnumerable(Of DiscordAutoCompleteChoice)) _
                Implements IAutocompleteProvider.Provider

            Dim choices As New List(Of DiscordAutoCompleteChoice)
            Try
                If ctx.Guild Is Nothing Then
                    Return Task.FromResult(Of IEnumerable(Of DiscordAutoCompleteChoice))(choices)
                End If

                Dim botPlugin = ctx.Services.GetService(Of DiscordBotPlugin)()
                If botPlugin Is Nothing Then
                    Return Task.FromResult(Of IEnumerable(Of DiscordAutoCompleteChoice))(choices)
                End If

                Dim instances = botPlugin.GetInstancesVisibleInGuild(ctx.Guild.Id.ToString())
                ' VB.Net keyword collision: 'partial' is reserved
                ' (Partial Class). Local renamed to filterText so
                ' the parser treats it as an identifier.
                Dim filterText = If(ctx.OptionValue Is Nothing, "", ctx.OptionValue.ToString())

                Dim filtered As IEnumerable(Of InstanceLookupEntry)
                If String.IsNullOrEmpty(filterText) Then
                    filtered = instances
                Else
                    filtered = instances.Where(
                        Function(x) x.DisplayName.IndexOf(filterText, StringComparison.OrdinalIgnoreCase) >= 0)
                End If

                For Each inst In filtered.Take(25)
                    ' Discord caps choice name at 100 chars.
                    Dim name = inst.DisplayName
                    If String.IsNullOrEmpty(name) Then name = inst.InstanceId
                    If name.Length > 100 Then name = name.Substring(0, 100)
                    choices.Add(New DiscordAutoCompleteChoice(name, inst.InstanceId))
                Next
            Catch
            End Try
            Return Task.FromResult(Of IEnumerable(Of DiscordAutoCompleteChoice))(choices)
        End Function

    End Class

    ' ============================================================
    '  Autocomplete provider for /lastseen's player argument.
    '
    '  Suggests distinct player names already in the history store
    '  (resolved DisplayName or raw persona), filtered by the
    '  partial typed string, capped at the Discord limit of 25.
    '  Deliberately NOT guild-scoped at the suggestion layer — the
    '  command body filters the RESULT to guild-visible instances,
    '  so a suggested name only ever seen elsewhere simply yields
    '  "no record ... visible in this server." Failures are silent:
    '  no suggestions, the user types the name manually.
    ' ============================================================

    Public Class LastSeenPlayerAutocompleteProvider
        Implements IAutocompleteProvider

        Public Async Function Provider(ctx As AutocompleteContext) _
                As Task(Of IEnumerable(Of DiscordAutoCompleteChoice)) _
                Implements IAutocompleteProvider.Provider

            Dim choices As New List(Of DiscordAutoCompleteChoice)
            Try
                If ctx.Guild Is Nothing Then Return choices

                Dim history = ctx.Services.GetService(Of HistoryQueryService)()
                If history Is Nothing Then Return choices

                Dim filterText = If(ctx.OptionValue Is Nothing, "", ctx.OptionValue.ToString())
                Dim names = Await history.GetKnownPlayerNamesAsync(Nothing)
                If names Is Nothing Then Return choices

                Dim filtered As IEnumerable(Of String)
                If String.IsNullOrEmpty(filterText) Then
                    filtered = names
                Else
                    filtered = names.Where(
                        Function(n) n.IndexOf(filterText, StringComparison.OrdinalIgnoreCase) >= 0)
                End If

                For Each n In filtered.Take(25)
                    Dim label = n
                    If label.Length > 100 Then label = label.Substring(0, 100)
                    choices.Add(New DiscordAutoCompleteChoice(label, n))
                Next
            Catch
            End Try
            Return choices
        End Function

    End Class

    ' ============================================================
    '  Autocomplete providers for /lastseen's game / installation
    '  scope arguments. Suggest distinct values from the Instances /
    '  Installations tables, filtered by the typed string. Not
    '  guild-scoped at the suggestion layer — the command body
    '  intersects the chosen scope with the guild-visible instance
    '  set, so an out-of-guild value just yields "nothing visible in
    '  that scope." Failures are silent.
    ' ============================================================

    Public Class GameAutocompleteProvider
        Implements IAutocompleteProvider

        Public Async Function Provider(ctx As AutocompleteContext) _
                As Task(Of IEnumerable(Of DiscordAutoCompleteChoice)) _
                Implements IAutocompleteProvider.Provider

            Dim choices As New List(Of DiscordAutoCompleteChoice)
            Try
                If ctx.Guild Is Nothing Then Return choices
                Dim filterText = If(ctx.OptionValue Is Nothing, "", ctx.OptionValue.ToString())

                Dim games As List(Of String)
                Using scope = ctx.Services.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    games = Await db.Instances.
                        Where(Function(i) i.GameId IsNot Nothing AndAlso i.GameId <> "").
                        Select(Function(i) i.GameId).
                        Distinct().
                        ToListAsync()
                End Using

                For Each g In games.
                        Where(Function(x) String.IsNullOrEmpty(filterText) OrElse
                              x.IndexOf(filterText, StringComparison.OrdinalIgnoreCase) >= 0).
                        Take(25)
                    Dim label = g
                    If label.Length > 100 Then label = label.Substring(0, 100)
                    choices.Add(New DiscordAutoCompleteChoice(label, g))
                Next
            Catch
            End Try
            Return choices
        End Function

    End Class

    Public Class InstallationAutocompleteProvider
        Implements IAutocompleteProvider

        Public Async Function Provider(ctx As AutocompleteContext) _
                As Task(Of IEnumerable(Of DiscordAutoCompleteChoice)) _
                Implements IAutocompleteProvider.Provider

            Dim choices As New List(Of DiscordAutoCompleteChoice)
            Try
                If ctx.Guild Is Nothing Then Return choices
                Dim filterText = If(ctx.OptionValue Is Nothing, "", ctx.OptionValue.ToString())

                ' Anonymous-typed projection is consumed inside the Using
                ' so its type stays inferred (Option Strict friendly).
                Using scope = ctx.Services.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    Dim installs = Await db.Installations.
                        OrderBy(Function(x) x.DisplayName).
                        Select(Function(x) New With {x.InstallationId, x.DisplayName}).
                        ToListAsync()
                    For Each ins In installs
                        Dim label = If(String.IsNullOrEmpty(ins.DisplayName),
                                       ins.InstallationId, ins.DisplayName)
                        If Not String.IsNullOrEmpty(filterText) AndAlso
                           label.IndexOf(filterText, StringComparison.OrdinalIgnoreCase) < 0 Then
                            Continue For
                        End If
                        If label.Length > 100 Then label = label.Substring(0, 100)
                        choices.Add(New DiscordAutoCompleteChoice(label, ins.InstallationId))
                        If choices.Count >= 25 Then Exit For
                    Next
                End Using
            Catch
            End Try
            Return choices
        End Function

    End Class

End Namespace
