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

        <SlashCommand("help", "Show available PowerGSM commands and panels in this server")>
        Public Async Function HelpAsync(ctx As InteractionContext) As Task
            Dim sb As New StringBuilder()
            sb.AppendLine("## PowerGSM commands")
            sb.AppendLine("• `/help` — this message")
            sb.AppendLine("• `/panels` — list the PowerGSM panels in this server")
            sb.AppendLine("• `/players <instance>` — show the player list for an instance (operators only)")
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

        <SlashCommand("panels", "List PowerGSM panels in this server")>
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

        <SlashCommand("players", "Show the player list for an instance")>
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
            Dim perm = botPlugin.ResolveUserPermission(ctx.Member, guildIdStr)
            If perm < CommandPermission.ServerOperator Then
                Await ReplyEphemeralAsync(ctx,
                    "You need a role mapped to ServerOperator (or higher) to run this command.")
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

            Await EditResponseAsync(ctx, BuildPlayersResponse(entry, players))
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
                Dim line As New StringBuilder()
                line.Append($"• **{EscapeForDiscord(p.Name)}**")
                If Not String.IsNullOrEmpty(p.Platform) Then
                    line.Append($" ({EscapeForDiscord(p.Platform)})")
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

End Namespace
