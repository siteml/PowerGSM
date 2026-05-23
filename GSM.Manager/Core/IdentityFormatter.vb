Imports System

' ============================================================
'  IdentityFormatter
'
'  Shared name-coalesce helper used wherever a player's
'  displayable name needs to be rendered. Three current
'  consumers as of Phase 5g-2:
'
'    1. HistoryQueryService.LoadTimeline — renders TimelineRow
'       rows assembled from PlayerActivityEntity (Join/Leave)
'       and ChatMessageEntity (Chat). Both entities now carry
'       DisplayName + PlayerName/PlatformPersona surfaces with
'       overlapping but not identical semantics, and the formatter
'       gives the renderer a single, consistent answer.
'
'    2. GsmSlashCommands.BuildPlayersResponse — Discord
'       /players slash command. Previously had its own inline
'       coalesce; switched to the shared helper so the answer
'       can't drift from History rendering's answer for the
'       same player.
'
'    3. (Implicit) any future caller that needs the same
'       answer — e.g. an InstancePanel live-player redraw,
'       or a future "now playing" status string.
'
'  Centralization rationale: the coalesce rule is one line of
'  logic, but if it lives inline in three places, three places
'  can drift independently as someone "improves" one without
'  touching the others. The History window and the Discord
'  bot were already disagreeing on a few formatting questions
'  in 5g-1 testing (mostly punctuation around platform
'  parentheticals); having one Format method to point at when
'  this happens again keeps the fix in one place.
'
'  Not made an instance class because the function is pure and
'  stateless — a Module exposes the same call surface without
'  forcing every consumer to inject a service for what's
'  effectively a static helper.
' ============================================================

Namespace GSM.Manager.Core

    ''' <summary>
    ''' Coalesces a player's identity strings into a single
    ''' displayable name. Used by HistoryQueryService and the
    ''' Discord /players command so the same player renders
    ''' identically across surfaces.
    '''
    ''' Three inputs, priority highest-to-lowest:
    '''
    ''' 1. <paramref name="displayName"/> — in-game character
    '''    name (e.g. "andre(qc)" on Last Oasis). Generally
    '''    stable across the character's lifetime; what other
    '''    players address them as in chat.
    '''
    ''' 2. <paramref name="platformPersona"/> — Steam handle /
    '''    Xbox gamertag (e.g. "andrekop"). Distinct from
    '''    displayName on Last Oasis by default — the
    '''    PlatformPersona is the player's Steam-account name,
    '''    not the character name they chose at creation. On
    '''    Factorio the two are the same; on most games where
    '''    there isn't a separate platform identity layer the
    '''    field is unused.
    '''
    ''' 3. <paramref name="fallback"/> — whatever raw string
    '''    the caller has on hand for rows where neither
    '''    resolved identity is available. Typically
    '''    PlayerActivityEntity.PlayerName (the raw parser
    '''    verdict) for History rendering, or the speaker name
    '''    embedded in a chat row.
    '''
    ''' Returns the first non-empty value, or empty string if
    ''' all three are missing.
    ''' </summary>
    Public Module IdentityFormatter

        ''' <summary>
        ''' Returns the best display name for a player given the
        ''' three sources. See module summary for the priority
        ''' rule and rationale. All inputs may be Nothing or
        ''' empty; returns empty string only when every input is
        ''' missing.
        ''' </summary>
        Public Function Format(displayName As String,
                                platformPersona As String,
                                fallback As String) As String
            If Not String.IsNullOrEmpty(displayName) Then Return displayName
            If Not String.IsNullOrEmpty(platformPersona) Then Return platformPersona
            If Not String.IsNullOrEmpty(fallback) Then Return fallback
            Return ""
        End Function

    End Module

End Namespace
