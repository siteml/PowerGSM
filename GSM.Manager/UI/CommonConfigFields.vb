Imports System.Collections.Generic
Imports GSM.Plugin

' ============================================================
'  CommonConfigFields — manager-owned schema fields
'
'  Describes config knobs that belong to the manager/node
'  runtime rather than to any one game plugin: crash-policy
'  tuning, restart timing, graceful shutdown timeout. Every
'  instance edit form merges these in after the game plugin's
'  schema so users have a single place to configure them.
'
'  Keys written here match what InstanceManager.StartInstanceAsync
'  and StopInstanceAsync read via GetIntField on the merged
'  customFields dictionary. Keep the two in sync.
'
'  Values are deliberately left blank by default — an empty
'  string in ConfigJson causes InstanceManager to fall back to
'  the hardcoded default, so we can introduce new knobs without
'  rewriting every existing instance's config on first save.
' ============================================================

Namespace GSM.Manager.UI

    Public Module CommonConfigFields

        ''' <summary>
        ''' Instance-level lifecycle fields: crash policy, restart
        ''' timing, shutdown timeout. Append this list to the output
        ''' of IGamePlugin.GetInstanceConfigSchema() at every render
        ''' site so the fields show up on every game's instance edit
        ''' form, regardless of plugin.
        ''' </summary>
        Public Function GetInstanceLifecycleFields() As List(Of ConfigFieldDescriptor)
            Return New List(Of ConfigFieldDescriptor) From {
                New ConfigFieldDescriptor With {
                    .Key = "MaxCrashCount",
                    .Label = "Max crashes before halt",
                    .Description = "How many crashes within the window below before the node stops auto-restarting. Leave blank for the default of 5.",
                    .FieldType = ConfigFieldType.IntegerField,
                    .DefaultValue = "",
                    .MinValue = 1,
                    .MaxValue = 1000
                },
                New ConfigFieldDescriptor With {
                    .Key = "CrashWindowMinutes",
                    .Label = "Crash window (minutes)",
                    .Description = "Sliding window (minutes) over which crashes are counted toward the halt threshold. Leave blank for the default of 60.",
                    .FieldType = ConfigFieldType.IntegerField,
                    .DefaultValue = "",
                    .MinValue = 1,
                    .MaxValue = 10080
                },
                New ConfigFieldDescriptor With {
                    .Key = "CrashCountResetAfterSeconds",
                    .Label = "Reset crash count after (seconds)",
                    .Description = "If the instance stays up continuously for this long after a (re)start, the crash counter resets to 0. Leave blank for the default of 300 (5 minutes). Set 0 to disable.",
                    .FieldType = ConfigFieldType.IntegerField,
                    .DefaultValue = "",
                    .MinValue = 0,
                    .MaxValue = 86400
                },
                New ConfigFieldDescriptor With {
                    .Key = "MinRestartDelayMs",
                    .Label = "Minimum restart delay (ms)",
                    .Description = "Floor applied to the exponential-backoff restart delay. Set to 4000+ to guarantee the crash notification is emitted before the node auto-restarts. Leave blank for no floor.",
                    .FieldType = ConfigFieldType.IntegerField,
                    .DefaultValue = "",
                    .MinValue = 0,
                    .MaxValue = 300000
                },
                New ConfigFieldDescriptor With {
                    .Key = "GracefulTimeoutMs",
                    .Label = "Graceful shutdown timeout (ms)",
                    .Description = "How long to wait for the server to exit after Ctrl+C / stdin-close before force-killing. Leave blank for the default of 25000 (25 seconds). Drop to 1000-2000 for games that don't support graceful shutdown.",
                    .FieldType = ConfigFieldType.IntegerField,
                    .DefaultValue = "",
                    .MinValue = 100,
                    .MaxValue = 300000
                },
                New ConfigFieldDescriptor With {
                    .Key = "ResurrectOnNodeBoot",
                    .Label = "Recover state on node restart",
                    .Description = "Only applies if this instance was RUNNING when the node went down: at node start, if its process is found dead (host reboot, or node and game killed together), it is restarted from its last-known launch settings. A stopped instance stays stopped — use 'Auto start with node' below for that. Works with the Manager offline. Applied to the node immediately on save when it's reachable; otherwise from this instance's next start. A restart loop is suppressed after 3 rapid attempts. Distinct from Auto-start (which is Manager-start-scoped).",
                    .FieldType = ConfigFieldType.BooleanField,
                    .DefaultValue = ""
                },
                New ConfigFieldDescriptor With {
                    .Key = "AutoStartOnNodeBoot",
                    .Label = "Auto start with node",
                    .Description = "Start this instance whenever the node service starts — even if it was cleanly stopped beforehand (like a Windows service set to Automatic). Works with the Manager offline. Applied to the node immediately on save when it's reachable — a stopped instance is armed by storing its launch settings on the node without starting it. The node keeps launch settings across stops once armed. A restart loop is suppressed after 3 rapid attempts. Distinct from Auto-start (Manager-start-scoped) and from 'Recover state on node restart' (which only restarts instances that were running).",
                    .FieldType = ConfigFieldType.BooleanField,
                    .DefaultValue = ""
                },
                New ConfigFieldDescriptor With {
                    .Key = "BootStartDelaySeconds",
                    .Label = "Node-boot start delay (seconds)",
                    .Description = "Stagger for the two node-boot options above: at node start, instances launch in ascending delay, each waiting until this many seconds have passed since the boot pass began (e.g. 0 / 30 / 60 brings three servers up a minute apart instead of all at once). Does not affect starts from the Manager. Applied to the node immediately on save when it's reachable. Leave blank or 0 for no delay.",
                    .FieldType = ConfigFieldType.IntegerField,
                    .DefaultValue = "",
                    .MinValue = 0,
                    .MaxValue = 3600
                }
            }
        End Function

    End Module

End Namespace