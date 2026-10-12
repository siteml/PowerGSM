Imports System
Imports System.Collections.Generic
Imports System.IO
Imports Microsoft.Extensions.Logging
Imports Microsoft.Extensions.Logging.Abstractions
Imports GSM.Plugin
Imports GSM.Node.Api

' ============================================================
'  EventStreamSelfTest — `GSM.Node --eventstream-selftest`
'  (EventStream Slice 2, verification gate Q4b)
'
'  Proves the source-authority dedupe in-process against a real
'  (throwaway) SQLite database, with no game, node service, or
'  Manager involved: with a source declared authoritative for
'  PlayerJoin/PlayerLeave, a second source emitting the same
'  kinds is suppressed at append, the suppressed counter
'  increments, the suppressed copy is NOT folded into the
'  roster, and lifecycle kinds cannot be claimed by a plugin
'  declaration. This is Q4b's "second source artificially
'  emitting PlayerJoin" made reproducible: the artificial second
'  source is literally an ingest call with a different SourceId
'  — exactly what ConPTY's parsed joins will be one day.
'
'  Modelled on --shim-self-test: console transcript, exit 0 on
'  PASS, 1 on any failed assertion.
' ============================================================

Namespace GSM.Node

    Friend Module EventStreamSelfTest

        Private _failures As Integer = 0

        Public Function Run() As Integer
            Console.WriteLine("=== EventStream self-test (authority dedupe, Q4b) ===")

            Dim tempDir = Path.Combine(Path.GetTempPath(),
                                       "gsm-eventstream-selftest-" & Guid.NewGuid().ToString("N"))
            Directory.CreateDirectory(tempDir)
            Try
                Dim db As New NodeDatabase(tempDir)
                db.EnsureCreated()
                Dim stream As New EventStreamStore(db, NullLogger(Of EventStreamStore).Instance)
                Dim store As New EventStore(db, stream, NullLogger(Of EventStore).Instance)

                Const instanceId As String = "selftest-instance"
                store.RegisterInstance(instanceId, New List(Of LogParseRule)())

                ' Authority: pal-rest owns join/leave; attempt to claim a
                ' lifecycle kind too (must be ignored).
                stream.SetAuthority(EventScopeType.Instance, instanceId,
                    New Dictionary(Of ParsedEventKind, String) From {
                        {ParsedEventKind.PlayerJoin, "pal-rest"},
                        {ParsedEventKind.PlayerLeave, "pal-rest"},
                        {ParsedEventKind.ProcessStarted, "pal-rest"}
                    })

                Dim joinFields As New Dictionary(Of String, String) From {
                    {"DisplayName", "TestPal"},
                    {"PlatformUserId", "76561198000000001"},
                    {"CharacterId", "ABCD1234"},
                    {"Platform", "steam"}
                }

                ' 1. Owner's join: appended + folded.
                Dim ok1 = store.IngestSynthesizedEvent(instanceId, ParsedEventKind.PlayerJoin,
                                                       joinFields, DateTime.UtcNow, "pal-rest")
                Check(ok1, "owner (pal-rest) PlayerJoin appended")
                Check(store.GetPlayers(instanceId).Count = 1, "roster has 1 player after owner join")

                ' 2. Second source, same fact: suppressed, not folded,
                '    roster unchanged (the Q4b core).
                Dim ok2 = store.IngestSynthesizedEvent(instanceId, ParsedEventKind.PlayerJoin,
                                                       joinFields, DateTime.UtcNow, "stdout")
                Check(Not ok2, "non-owner (stdout) PlayerJoin did NOT append")
                Check(store.GetPlayers(instanceId).Count = 1, "roster still 1 (suppressed copy not folded)")

                Dim diags = stream.GetSourceDiagnostics(instanceId)
                Dim stdoutDiag = diags.Find(Function(d) d.SourceId = "stdout")
                Dim palDiag = diags.Find(Function(d) d.SourceId = "pal-rest")
                Check(stdoutDiag IsNot Nothing AndAlso stdoutDiag.Suppressed = 1,
                      "suppressed counter incremented for stdout (got " &
                      If(stdoutDiag IsNot Nothing, stdoutDiag.Suppressed.ToString(), "n/a") & ")")
                Check(palDiag IsNot Nothing AndAlso palDiag.Appended >= 1,
                      "appended counter incremented for pal-rest")
                Check(palDiag IsNot Nothing AndAlso palDiag.OwnedKinds.Contains("PlayerJoin"),
                      "authority map reports pal-rest owns PlayerJoin")

                ' 3. Lifecycle protection: plugin claim on ProcessStarted was
                '    ignored; "lifecycle" appends fine, pal-rest cannot.
                Dim okLife = store.IngestSynthesizedEvent(instanceId, ParsedEventKind.ProcessStarted,
                    New Dictionary(Of String, String) From {{"Pid", "1"}}, DateTime.UtcNow, "lifecycle")
                Check(okLife, "lifecycle source appends ProcessStarted despite plugin claim attempt")
                Dim okSteal = store.IngestSynthesizedEvent(instanceId, ParsedEventKind.ProcessStarted,
                    New Dictionary(Of String, String) From {{"Pid", "2"}}, DateTime.UtcNow, "pal-rest")
                Check(Not okSteal, "pal-rest cannot append lifecycle kinds")

                ' 4. Unowned kind stays first-come-append: chat from any source.
                Dim okChat = store.IngestSynthesizedEvent(instanceId, ParsedEventKind.ChatMessage,
                    New Dictionary(Of String, String) From {
                        {"DisplayName", "TestPal"}, {"Message", "hello"}},
                    DateTime.UtcNow, "stdout")
                Check(okChat, "unowned kind (ChatMessage) still first-come-append from any source")

                ' 5. Owner's leave folds the roster empty.
                Dim ok5 = store.IngestSynthesizedEvent(instanceId, ParsedEventKind.PlayerLeave,
                                                       joinFields, DateTime.UtcNow, "pal-rest")
                Check(ok5, "owner PlayerLeave appended")
                Check(store.GetPlayers(instanceId).Count = 0, "roster empty after owner leave")

                stream.Dispose()
            Catch ex As Exception
                Console.WriteLine("FAIL: unhandled exception: " & ex.ToString())
                _failures += 1
            Finally
                Try
                    Directory.Delete(tempDir, recursive:=True)
                Catch
                End Try
            End Try

            If _failures = 0 Then
                Console.WriteLine("=== EventStream self-test PASS ===")
                Return 0
            End If
            Console.WriteLine($"=== EventStream self-test FAIL ({_failures} assertion(s)) ===")
            Return 1
        End Function

        Private Sub Check(condition As Boolean, description As String)
            If condition Then
                Console.WriteLine("  ok   " & description)
            Else
                Console.WriteLine("  FAIL " & description)
                _failures += 1
            End If
        End Sub

    End Module

End Namespace
