' ============================================================
'  GSM.Node — --dump-watermarks / --clear-watermarks
'
'  Direct node.db diagnostics for the event purge-watermark gate
'  (EventStream Slice 3), added 2026-08-12 after a live hunt: a
'  purge test with an explicit future cutoff had left watermarks
'  that silently dropped every new PlayerJoin/PlayerLeave/
'  ChatMessage append on the instance ("console line visible,
'  event nowhere"). sqlite3.exe isn't stock Windows, so the node
'  carries its own inspector:
'
'    GSM.Node --dump-watermarks  [--data-dir <path>]
'    GSM.Node --clear-watermarks <instanceId> [--data-dir <path>]
'
'  Dump prints every watermark row (flagging FUTURE ones — those
'  block ALL new events of that kind forever, since watermarks
'  only move forward) plus per-kind event counts. Clear deletes
'  an instance's watermark rows; the node service must be
'  RESTARTED afterwards (watermarks are cached in memory per
'  scope once loaded).
' ============================================================
Imports System
Imports System.IO
Imports Microsoft.Data.Sqlite
Imports GSM.Plugin

Namespace GSM.Node

    Friend Module WatermarkDiag

        Public Function Run(args As String()) As Integer
            Dim dataDir As String = Path.Combine(AppContext.BaseDirectory, "data")
            Dim di = Array.IndexOf(args, "--data-dir")
            If di >= 0 AndAlso di + 1 < args.Length Then dataDir = args(di + 1)
            Dim dbPath = Path.Combine(dataDir, "node.db")
            If Not File.Exists(dbPath) Then
                Console.WriteLine($"[watermarks] node.db not found at {dbPath} (pass --data-dir <path>)")
                Return 2
            End If

            Try
                ' Safe against a RUNNING node service: dumps are plain reads,
                ' and the busy timeout lets the clear's DELETE wait out any
                ' in-flight node write instead of failing with "locked".
                Using conn As New SqliteConnection($"Data Source={dbPath}")
                    conn.DefaultTimeout = 10
                    conn.Open()

                    Dim ci = Array.IndexOf(args, "--clear-watermarks")
                    If ci >= 0 Then
                        Dim scopeId As String = Nothing
                        If ci + 1 < args.Length AndAlso Not args(ci + 1).StartsWith("--") Then
                            scopeId = args(ci + 1)
                        End If
                        If String.IsNullOrEmpty(scopeId) Then
                            Console.WriteLine("[watermarks] usage: GSM.Node --clear-watermarks <instanceId>")
                            Return 2
                        End If
                        Using cmd = conn.CreateCommand()
                            cmd.CommandText = "DELETE FROM event_purge_watermarks WHERE scope_id = $sid"
                            cmd.Parameters.AddWithValue("$sid", scopeId)
                            Dim n = cmd.ExecuteNonQuery()
                            Console.WriteLine($"[watermarks] deleted {n} watermark row(s) for {scopeId}")
                            Console.WriteLine("[watermarks] RESTART the node service now — it caches watermarks in memory per instance")
                        End Using
                        Return 0
                    End If

                    Console.WriteLine($"[watermarks] db: {dbPath}")
                    Dim nowStr = DateTime.UtcNow.ToString("o")
                    Console.WriteLine($"[watermarks] now (utc, event format): {nowStr}")

                    Using cmd = conn.CreateCommand()
                        cmd.CommandText = "SELECT scope_type, scope_id, kind, purged_through_utc
                                           FROM event_purge_watermarks
                                           ORDER BY scope_id, kind"
                        Using r = cmd.ExecuteReader()
                            Dim any As Boolean = False
                            While r.Read()
                                any = True
                                Dim kindInt = r.GetInt32(2)
                                Dim kindName = CType(kindInt, ParsedEventKind).ToString()
                                Dim wm = r.GetString(3)
                                Dim future = String.Compare(wm, nowStr, StringComparison.Ordinal) > 0
                                Console.WriteLine(
                                    $"[watermarks] scope={r.GetString(1)}  kind={kindName}({kindInt})  through={wm}" &
                                    If(future, "   <<< FUTURE — blocks ALL new events of this kind", ""))
                            End While
                            If Not any Then
                                Console.WriteLine("[watermarks] no watermark rows — the purge gate is NOT the blocker")
                            End If
                        End Using
                    End Using

                    Console.WriteLine("[watermarks] events by kind:")
                    Using cmd = conn.CreateCommand()
                        cmd.CommandText = "SELECT scope_id, kind, COUNT(*), MAX(utc_at)
                                           FROM events
                                           GROUP BY scope_id, kind
                                           ORDER BY scope_id, kind"
                        Using r = cmd.ExecuteReader()
                            While r.Read()
                                Dim kindName = CType(r.GetInt32(1), ParsedEventKind).ToString()
                                Console.WriteLine(
                                    $"[watermarks]   {r.GetString(0)}  {kindName} x{r.GetInt64(2)}  last={r.GetString(3)}")
                            End While
                        End Using
                    End Using
                End Using
                Return 0
            Catch ex As Exception
                Console.WriteLine("[watermarks] failed: " & ex.Message)
                Return 1
            End Try
        End Function

    End Module

End Namespace
