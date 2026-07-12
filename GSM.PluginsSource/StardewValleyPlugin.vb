' <plugin id="stardewvalley" name="Stardew Valley" version="0.1.0" author="siteml" requiresContracts="2">
' <RequiresContracts: 2>
' ============================================================
'  Stardew Valley plugin — headless server via SMAPI +
'  siteml/SMAPIDedicatedServerMod fork.
'
'  Slice 3 skeleton: install steps, schemas, launch basics.
'  Config.json generation + launch options land in Slice 4;
'  log parse rules in Slice 5. See StardewValley_Plugin_Plan.md.
'
'  Concurrency: vanilla SDV hardcodes UDP port 24642, so only
'  one running instance per node. MaxInstancesPerInstallation
'  is 1 accordingly (see plan, Tier 4 for the Harmony unlock).
' ============================================================

Imports System
Imports System.Collections.Generic
Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports GSM.Plugin

Public Class StardewValleyPlugin
    Implements IGamePlugin
    Implements ILaunchOptionsProvider
    Implements IStartupFileProvider
    Implements IManagedDirectoriesProvider
    Implements IFileGenerationProvider
    Implements IPrerequisiteProvider

    ' Default artifact URLs. Both overridable per-installation via
    ' install config fields of the same name.
    Private Const DefaultModDownloadUrl As String =
        "https://github.com/siteml/SMAPIDedicatedServerMod/releases/download/pgsm-v1.0.0/DedicatedServer.1.2.3.zip"
    Private Const DefaultSmapiDownloadUrl As String =
        "https://github.com/Pathoschild/SMAPI/releases/download/4.5.2/SMAPI-4.5.2-installer.zip"
    Private Const DefaultMesaDownloadUrl As String =
        "https://github.com/pal1000/mesa-dist-win/releases/download/26.1.3/mesa3d-26.1.3-release-msvc.7z"

    Public ReadOnly Property GameId As String Implements IGamePlugin.GameId
        Get
            Return "stardewvalley"
        End Get
    End Property

    Public ReadOnly Property DisplayName As String Implements IGamePlugin.DisplayName
        Get
            Return "Stardew Valley"
        End Get
    End Property

    ' One farm per installation: saves are keyed by FarmName under a
    ' shared per-OS-user saves directory, and the game port is fixed
    ' at 24642, so a second concurrent instance can never bind anyway.
    Public ReadOnly Property MaxInstancesPerInstallation As Integer? Implements IGamePlugin.MaxInstancesPerInstallation
        Get
            Return 1
        End Get
    End Property

    Public Function GetSupportedInstallMethods() As IReadOnlyList(Of InstallMethod) Implements IGamePlugin.GetSupportedInstallMethods
        Return New List(Of InstallMethod) From {InstallMethod.SteamCmd}
    End Function

    Public Function GetInstallSteps(config As InstallationConfig) As IReadOnlyList(Of InstallStep) Implements IGamePlugin.GetInstallSteps
        Dim steps As New List(Of InstallStep)

        ' 1. Game files. SDV has no dedicated-server depot and no
        '    anonymous branch — a Steam account owning appid 413150
        '    is required (RequiresLogin), same credential flow as
        '    Last Oasis.
        steps.Add(New SteamCmdStep With {
            .StepName = "Install Stardew Valley (Steam)",
            .AppId = 413150,
            .RequiresLogin = True,
            .Platform = If(config.Platform = NodePlatform.Linux, "linux", "windows")
        })

        ' 2. SMAPI installer zip → staging subfolder.
        steps.Add(New DownloadFileStep With {
            .StepName = "Download SMAPI",
            .Url = GetField(config.CustomFields, "SmapiDownloadUrl", DefaultSmapiDownloadUrl),
            .DestinationRelativePath = "smapi-installer.zip",
            .ExtractArchive = True,
            .StripTopLevelDirectory = True,
            .ExtractToRelativePath = "gsm-smapi-installer"
        })

        ' 3. Run SMAPI installer unattended into the game dir.
        '    VERIFY AT FIRST LIVE INSTALL: unattended flags are
        '    documented as "--install --game-path" in SMAPI's
        '    technical docs; adjust here if the installer prompts.
        '    RunProcessStep does no token substitution and resolves
        '    relative FileName against the node process's own CWD,
        '    so both paths are built absolute from config.InstallPath.
        '    Separators are built per-platform BY HAND — Path.Combine
        '    runs on the Manager (Windows) and would emit backslashes
        '    into paths destined for a Linux node.
        If config.Platform = NodePlatform.Linux Then
            Dim installerRoot = config.InstallPath.TrimEnd("/"c) & "/gsm-smapi-installer"
            Dim installerExe = installerRoot & "/internal/linux/SMAPI.Installer"
            ' Zip extraction doesn't preserve unix modes — restore the
            ' exec bit before launching the installer.
            steps.Add(New RunProcessStep With {
                .StepName = "Mark SMAPI installer executable",
                .ExecutablePath = "/bin/sh",
                .Arguments = "-c ""chmod +x '" & installerExe & "'""",
                .WorkingDirectory = installerRoot,
                .TimeoutMs = 30000
            })
            steps.Add(New RunProcessStep With {
                .StepName = "Install SMAPI",
                .Arguments = "--install --no-prompt --game-path """ & config.InstallPath & """",
                .ExecutablePath = installerExe,
                .WorkingDirectory = installerRoot,
                .TimeoutMs = 300000,
                .RequiresRealConsole = True
            })
        Else
            Dim installerSubdir = IO.Path.Combine(config.InstallPath, "gsm-smapi-installer")
            steps.Add(New RunProcessStep With {
                .StepName = "Install SMAPI",
                .Arguments = "--install --no-prompt --game-path """ & config.InstallPath & """",
                .ExecutablePath = IO.Path.Combine(installerSubdir, "internal", "windows", "SMAPI.Installer.exe"),
                .WorkingDirectory = installerSubdir,
                .TimeoutMs = 300000,
                .RequiresRealConsole = True
            })
        End If

        ' 4. Dedicated-server mod zip → Mods\. Zip root is the
        '    "DedicatedServer/" folder, so extracting into Mods
        '    lands it at Mods\DedicatedServer\ as SMAPI expects.
        steps.Add(New DownloadFileStep With {
            .StepName = "Install dedicated server mod",
            .Url = GetField(config.CustomFields, "ModDownloadUrl", DefaultModDownloadUrl),
            .DestinationRelativePath = "gsm-servermod.zip",
            .ExtractArchive = True,
            .ExtractToRelativePath = "Mods"
        })

        ' 5. Windows + SoftwareRendering: Mesa llvmpipe software GL.
        '    GPU-less nodes (rack servers, VMs) can't create a
        '    MonoGame graphics device otherwise. Two dlls dropped
        '    beside the game exe + the GALLIUM_DRIVER env var at
        '    launch (see GetLaunchOptions) — both are required;
        '    without the env var mesa auto-picks a d3d12/WARP path
        '    that exits silently after the title screen.
        If config.Platform <> NodePlatform.Linux AndAlso
           IsTrue(GetField(config.CustomFields, "SoftwareRendering", "true")) Then
            steps.Add(New DownloadFileStep With {
                .StepName = "Download Mesa (software rendering)",
                .Url = GetField(config.CustomFields, "MesaDownloadUrl", DefaultMesaDownloadUrl),
                .DestinationRelativePath = "gsm-mesa.7z",
                .ExtractArchive = True,
                .ExtractToRelativePath = "gsm-mesa",
                .ExtractOnlyPaths = New List(Of String) From {
                    "x64/opengl32.dll",
                    "x64/libgallium_wgl.dll"
                }
            })
            steps.Add(New CopyFileStep With {
                .StepName = "Place Mesa opengl32.dll",
                .SourceRelativePath = "gsm-mesa/x64/opengl32.dll",
                .DestinationRelativePath = "opengl32.dll"
            })
            steps.Add(New CopyFileStep With {
                .StepName = "Place Mesa libgallium_wgl.dll",
                .SourceRelativePath = "gsm-mesa/x64/libgallium_wgl.dll",
                .DestinationRelativePath = "libgallium_wgl.dll"
            })
        End If

        Return steps
    End Function

    Public Function GetUpdateSteps(config As InstallationConfig) As IReadOnlyList(Of InstallStep) Implements IGamePlugin.GetUpdateSteps
        ' Same pipeline: SteamCMD validates/updates game files (which
        ' can overwrite nothing SMAPI-related — SMAPI lives alongside),
        ' then SMAPI + mod are reinstalled at their configured URLs so
        ' bumping a URL field and pressing Update rolls the mod forward.
        Return GetInstallSteps(config)
    End Function

    Public Function BuildLaunchArguments(config As InstanceConfig) As String Implements IGamePlugin.BuildLaunchArguments
        ' SMAPI itself needs no arguments; all behaviour comes from the
        ' mod's config.json. Linux runs through a tiny sh bootstrap:
        ' MonoGame needs a display, but xvfb-run is a WRAPPER SCRIPT —
        ' the spawned pid would be the script, and the node's graceful
        ' SIGINT would hit the wrapper instead of SMAPI (observed:
        ' stop didn't stop the game). Instead: start a shared Xvfb on
        ' display :97 if one isn't already up (the X lock file is the
        ' idempotence check; the daemon deliberately outlives the game
        ' and is reused by every later start), then EXEC SMAPI so the
        ' shell replaces itself — the shim's spawned pid IS SMAPI and
        ' SIGINT lands on the right process. DISPLAY rides in via
        ' LaunchOptions.EnvironmentVars.
        If config.Platform = NodePlatform.Linux Then
            Return "-c ""[ -e /tmp/.X97-lock ] || Xvfb :97 -screen 0 1280x720x24 & sleep 1; exec ./StardewModdingAPI"""
        End If
        Return ""
    End Function

    Public Function ValidateConfig(config As InstanceConfig) As IReadOnlyList(Of String) Implements IGamePlugin.ValidateConfig
        Dim errors As New List(Of String)
        Dim farmName = GetField(config.CustomFields, "FarmName", "")
        If String.IsNullOrWhiteSpace(farmName) Then
            errors.Add("FarmName is required — it selects (or creates) the save the server hosts.")
        End If
        Return errors
    End Function

    Public Function GetInstallConfigSchema() As IReadOnlyList(Of ConfigFieldDescriptor) Implements IGamePlugin.GetInstallConfigSchema
        Return New List(Of ConfigFieldDescriptor) From {
            New ConfigFieldDescriptor With {
                .Key = "ServerMod",
                .Label = "Server mod",
                .Description = "Which dedicated-server mod to install. 'headless' is the PowerGSM fork (recommended).",
                .FieldType = ConfigFieldType.[Enum],
                .EnumValues = New List(Of String) From {"headless"},
                .DefaultValue = "headless"
            },
            New ConfigFieldDescriptor With {
                .Key = "ModDownloadUrl",
                .Label = "Server mod download URL",
                .Description = "Zip of the dedicated-server SMAPI mod (mod folder at zip root). Bump this to roll the mod forward, then run Update.",
                .FieldType = ConfigFieldType.Text,
                .DefaultValue = DefaultModDownloadUrl
            },
            New ConfigFieldDescriptor With {
                .Key = "SmapiDownloadUrl",
                .Label = "SMAPI installer URL",
                .Description = "SMAPI installer zip. Pin to a version known to work with the installed game version.",
                .FieldType = ConfigFieldType.Text,
                .DefaultValue = DefaultSmapiDownloadUrl
            },
            New ConfigFieldDescriptor With {
                .Key = "SoftwareRendering",
                .Label = "Software rendering (Mesa llvmpipe)",
                .Description = "Install Mesa software OpenGL and force llvmpipe at launch. Required on Windows nodes without a working GPU (rack servers, most VMs). Ignored on Linux (xvfb covers it). Turn off only if the node has a real GPU.",
                .FieldType = ConfigFieldType.BooleanField,
                .DefaultValue = "true"
            },
            New ConfigFieldDescriptor With {
                .Key = "MesaDownloadUrl",
                .Label = "Mesa download URL",
                .Description = "mesa-dist-win release archive used when software rendering is enabled.",
                .FieldType = ConfigFieldType.Text,
                .DefaultValue = DefaultMesaDownloadUrl
            }
        }
    End Function

    Public Function GetInstanceConfigSchema() As IReadOnlyList(Of ConfigFieldDescriptor) Implements IGamePlugin.GetInstanceConfigSchema
        Return New List(Of ConfigFieldDescriptor) From {
            New ConfigFieldDescriptor With {
                .Key = "FarmName",
                .Label = "Farm name",
                .Description = "Save to host. If no save with this name exists, the server creates a new farm with it.",
                .FieldType = ConfigFieldType.Text,
                .IsRequired = True
            },
            New ConfigFieldDescriptor With {
                .Key = "Port",
                .Label = "Game port (fixed)",
                .Description = "Vanilla Stardew hardcodes UDP 24642 and offers no way to change it — this field exists so PowerGSM's port allocator knows the port is taken (two Stardew servers cannot share a node; see the Tier-4 Harmony unlock in the plugin roadmap).",
                .FieldType = ConfigFieldType.IntegerField,
                .DefaultValue = "24642",
                .MinValue = 24642,
                .MaxValue = 24642,
                .IsPort = True
            },
            New ConfigFieldDescriptor With {
                .Key = "StartingCabins",
                .Label = "Starting cabins (new farms only)",
                .Description = "Cabins built when a NEW farm is created (existing saves are unaffected).",
                .FieldType = ConfigFieldType.IntegerField,
                .DefaultValue = "3",
                .MinValue = 0,
                .MaxValue = 3
            },
            New ConfigFieldDescriptor With {
                .Key = "CabinLayout",
                .Label = "Cabin layout (new farms only)",
                .FieldType = ConfigFieldType.[Enum],
                .EnumValues = New List(Of String) From {"separate", "nearby"},
                .DefaultValue = "separate"
            },
            New ConfigFieldDescriptor With {
                .Key = "ProfitMargin",
                .Label = "Profit margin (new farms only)",
                .FieldType = ConfigFieldType.[Enum],
                .EnumValues = New List(Of String) From {"normal", "75%", "50%", "25%"},
                .DefaultValue = "normal"
            },
            New ConfigFieldDescriptor With {
                .Key = "MoneyStyle",
                .Label = "Money style (new farms only)",
                .FieldType = ConfigFieldType.[Enum],
                .EnumValues = New List(Of String) From {"shared", "separate"},
                .DefaultValue = "shared"
            },
            New ConfigFieldDescriptor With {
                .Key = "FarmType",
                .Label = "Farm type (new farms only)",
                .FieldType = ConfigFieldType.[Enum],
                .EnumValues = New List(Of String) From {"standard", "riverland", "forest", "hilltop", "wilderness", "fourcorners", "beach"},
                .DefaultValue = "standard"
            },
            New ConfigFieldDescriptor With {
                .Key = "CommunityCenterBundles",
                .Label = "Community center bundles (new farms only)",
                .FieldType = ConfigFieldType.[Enum],
                .EnumValues = New List(Of String) From {"normal", "remixed"},
                .DefaultValue = "normal"
            },
            New ConfigFieldDescriptor With {
                .Key = "GuaranteeYear1Completable",
                .Label = "Guarantee year 1 completable (new farms only)",
                .FieldType = ConfigFieldType.BooleanField,
                .DefaultValue = "false"
            },
            New ConfigFieldDescriptor With {
                .Key = "MineRewards",
                .Label = "Mine rewards (new farms only)",
                .FieldType = ConfigFieldType.[Enum],
                .EnumValues = New List(Of String) From {"normal", "remixed"},
                .DefaultValue = "normal"
            },
            New ConfigFieldDescriptor With {
                .Key = "RandomSeed",
                .Label = "Random seed (new farms only)",
                .Description = "Optional world-generation seed. Leave empty for random.",
                .FieldType = ConfigFieldType.Text
            },
            New ConfigFieldDescriptor With {
                .Key = "PetBreed",
                .Label = "Pet breed (new farms only)",
                .Description = "0-4 = cat, 5-9 = dog, -1 = no pet.",
                .FieldType = ConfigFieldType.IntegerField,
                .DefaultValue = "0",
                .MinValue = -1,
                .MaxValue = 9
            },
            New ConfigFieldDescriptor With {
                .Key = "PetName",
                .Label = "Pet name (new farms only)",
                .FieldType = ConfigFieldType.Text,
                .DefaultValue = "Stella"
            },
            New ConfigFieldDescriptor With {
                .Key = "MushroomsOrBats",
                .Label = "Mushrooms or bats (new farms only)",
                .FieldType = ConfigFieldType.[Enum],
                .EnumValues = New List(Of String) From {"Mushrooms", "Bats"},
                .DefaultValue = "Mushrooms"
            },
            New ConfigFieldDescriptor With {
                .Key = "EnableCropSaver",
                .Label = "Crop saver",
                .Description = "Prevents crops dying when no one plays for a while.",
                .FieldType = ConfigFieldType.BooleanField,
                .DefaultValue = "true"
            },
            New ConfigFieldDescriptor With {
                .Key = "PurchaseJojaMembership",
                .Label = "Purchase Joja membership",
                .Description = "Host bot buys a Joja membership when available — commits to the Joja route and removes the community center.",
                .FieldType = ConfigFieldType.BooleanField,
                .DefaultValue = "false"
            },
            New ConfigFieldDescriptor With {
                .Key = "SpawnMonstersOnFarmAtNight",
                .Label = "Monsters on farm at night",
                .FieldType = ConfigFieldType.BooleanField,
                .DefaultValue = "false"
            },
            New ConfigFieldDescriptor With {
                .Key = "MoveBuildPermission",
                .Label = "Farmhand building-move permission",
                .Description = "off = disabled, owned = own buildings only, on = all buildings.",
                .FieldType = ConfigFieldType.[Enum],
                .EnumValues = New List(Of String) From {"off", "owned", "on"},
                .DefaultValue = "off"
            },
            New ConfigFieldDescriptor With {
                .Key = "TryActivatingInviteCode",
                .Label = "Try activating invite code",
                .Description = "Only works when the game can reach Steam/GOG networking; PowerGSM servers usually run steamless (direct IP joins).",
                .FieldType = ConfigFieldType.BooleanField,
                .DefaultValue = "true"
            },
            New ConfigFieldDescriptor With {
                .Key = "UpgradeHouseLevelBasedOnFarmhand",
                .Label = "Auto-upgrade host farmhouse",
                .Description = "Keeps the host's farmhouse at the highest upgrade level of all farmers (cellar unlock etc.).",
                .FieldType = ConfigFieldType.BooleanField,
                .DefaultValue = "false"
            },
            New ConfigFieldDescriptor With {
                .Key = "Password",
                .Label = "Chat command password",
                .Description = "Password protecting the mod's in-game chat commands (empty = no password).",
                .FieldType = ConfigFieldType.Text
            }
        }
    End Function

    Public Function EvaluateCrash(exitCode As Integer,
                                  crashCount As Integer,
                                  policy As CrashRestartPolicy) As RestartDecision Implements IGamePlugin.EvaluateCrash
        ' 0 = clean (PGSM graceful stop exits 0) — never restart.
        ' Anything else: restart, letting the node's policy machinery
        ' (max count / window, enforced node-side) rein it in.
        If exitCode = 0 Then
            Return RestartDecision.Halt("Clean exit")
        End If
        Return RestartDecision.Restart(delayMs:=5000,
                                       reason:=$"Exited with code {exitCode}")
    End Function

    Public Function CreateLogParser() As ILogParser Implements IGamePlugin.CreateLogParser
        ' Manager-side parser: drives join/leave notifications and
        ' player-event history rows (the declarative node rules feed
        ' the node's own player/chat tracking, but the Manager's
        ' HandlePlayerJoin/Leave + history persistence run off this
        ' legacy path — same as Factorio/LO).
        Return New StardewLogParser()
    End Function

    Public Function GetLogSources(config As InstanceConfig) As IReadOnlyList(Of ILogSource) Implements IGamePlugin.GetLogSources
        ' SMAPI writes everything of interest to stdout; the [PGSM]
        ' structured lines ride the same stream.
        Return New List(Of ILogSource) From {New StdoutLogSource()}
    End Function

    Public Function GetLogParseRules() As IReadOnlyList(Of LogParseRule) Implements IGamePlugin.GetLogParseRules
        ' [PGSM] structured lines from the fork (see PGSM_CHANGES.md).
        ' Named groups built via concatenation per project convention.
        Dim rules As New List(Of LogParseRule)

        ' JOIN name="Testy" id="2012480384129362318"
        rules.Add(New LogParseRule With {
            .Kind = ParsedEventKind.PlayerJoin,
            .Pattern = "\[PGSM\] JOIN name=""(?<" & "DisplayName" & ">[^""]*)"" id=""(?<" & "PlatformUserId" & ">-?\d+)"""
        })

        ' LEAVE name="Testy" id="..."
        rules.Add(New LogParseRule With {
            .Kind = ParsedEventKind.PlayerLeave,
            .Pattern = "\[PGSM\] LEAVE name=""(?<" & "DisplayName" & ">[^""]*)"" id=""(?<" & "PlatformUserId" & ">-?\d+)"""
        })

        ' Upstream system notice carries the remote address (the only
        ' source of it) and fires just BEFORE the JOIN line. Classified
        ' as PlayerJoin with ONLY RemoteAddress captured: EventStore's
        ' addr-only join path stashes the IP (PendingRemoteAddress) and
        ' the immediately-following real JOIN claims it. DisplayName is
        ' deliberately NOT captured here — capturing it would make this
        ' look like a real join and create the session prematurely.
        rules.Add(New LogParseRule With {
            .Kind = ParsedEventKind.PlayerJoin,
            .Pattern = "\[PGSM\] CHAT name=""ServerBot"" id=""0"" kind=""2"" msg=""[^""(]+? \((?<" & "RemoteAddress" & ">[0-9a-fA-F\.:]+)\) has joined\."
        })

        ' Real player chat: kind="0" from anyone who is not the bot.
        ' Negative lookahead excludes ServerBot's announcement spam.
        rules.Add(New LogParseRule With {
            .Kind = ParsedEventKind.ChatMessage,
            .Pattern = "\[PGSM\] CHAT name=""(?<" & "DisplayName" & ">(?!ServerBot"")[^""]*)"" id=""(?<" & "PlatformUserId" & ">-?\d+)"" kind=""0"" msg=""(?<" & "Message" & ">.*)""$"
        })

        ' Day rollover → server state. MatchState captures the raw
        ' season/day/year triple (schema has no calendar fields);
        ' a cleaner pre-composed [PGSM] STATE line is a future fork
        ' nicety.
        rules.Add(New LogParseRule With {
            .Kind = ParsedEventKind.ServerStateChange,
            .Pattern = "\[PGSM\] DAY (?<" & "MatchState" & ">season=""[^""]+"" day=""\d+"" year=""\d+"")"
        })

        Return rules
    End Function

    Public Function GetExecutablePath(config As InstanceConfig) As IReadOnlyList(Of String) Implements IGamePlugin.GetExecutablePath
        ' Launch SMAPI, not the game exe — SMAPI chain-loads the game
        ' with mods injected. Linux goes through /bin/sh (rooted — the
        ' Manager passes rooted candidates through untouched) which
        ' ensures Xvfb is up and then execs SMAPI; see
        ' BuildLaunchArguments for the graceful-stop rationale.
        ' Requires the xvfb package on the node (`apt install xvfb`).
        If config.Platform = NodePlatform.Linux Then
            Return New List(Of String) From {"/bin/sh"}
        End If
        Return New List(Of String) From {
            "StardewModdingAPI.exe",
            "StardewModdingAPI"
        }
    End Function

    Public Function GetRconProtocol() As RconProtocol? Implements IGamePlugin.GetRconProtocol
        Return Nothing
    End Function

    ' Linux prereqs surfaced as pre-install notices: xvfb (virtual
    ' display for MonoGame's graphics init) and unzip (Farm Save
    ' Archive restore of Windows-made .zip archives; python3
    ' satisfies it too). The node's probe returns "satisfied" for
    ' both on Windows nodes, so declaring unconditionally is safe.
    Public Function GetRequiredPrerequisites() As IReadOnlyList(Of String) Implements IPrerequisiteProvider.GetRequiredPrerequisites
        Return New List(Of String) From {"linux-xvfb", "linux-unzip"}
    End Function

    Public Function GetLaunchOptions(config As InstanceConfig) As LaunchOptions Implements ILaunchOptionsProvider.GetLaunchOptions
        Dim opts As New LaunchOptions With {
            .StdoutIsLog = True,
            .GracefulShutdownTimeoutMs = 60000
        }
        ' Install-level SoftwareRendering reaches us via the merged
        ' CustomFields (installation ConfigJson overlays into
        ' InstanceConfig.CustomFields before plugin calls).
        ' Windows: GALLIUM_DRIVER pairs with the mesa dlls the install
        ' step placed. Linux: LIBGL_ALWAYS_SOFTWARE forces the distro's
        ' Mesa onto llvmpipe under xvfb — no install step needed, the
        ' system GL stack provides it (libgl1 + mesa dri drivers).
        If IsTrue(GetField(config.CustomFields, "SoftwareRendering", "true")) Then
            If config.Platform = NodePlatform.Linux Then
                opts.EnvironmentVars = New Dictionary(Of String, String) From {
                    {"LIBGL_ALWAYS_SOFTWARE", "1"},
                    {"DISPLAY", ":97"}
                }
            Else
                opts.EnvironmentVars = New Dictionary(Of String, String) From {
                    {"GALLIUM_DRIVER", "llvmpipe"}
                }
            End If
        ElseIf config.Platform = NodePlatform.Linux Then
            ' DISPLAY is needed regardless of the software-rendering
            ' toggle — the Xvfb bootstrap always targets :97.
            opts.EnvironmentVars = New Dictionary(Of String, String) From {
                {"DISPLAY", ":97"}
            }
        End If
        Return opts
    End Function

    ' ------------------------------------------------------------
    '  IStartupFileProvider — renders the dedicated-server mod's
    '  config.json from the merged instance config on every start.
    '  The file is plugin-owned: the full ModConfig field set is
    '  regenerated, so hand-edits on the node don't survive a start
    '  (per the interface's single-ownership rule).
    ' ------------------------------------------------------------

    Private Const ModConfigRelPath As String = "Mods/DedicatedServer/config.json"

    Public Function GetStartupFiles(instanceConfig As InstanceConfig) As IReadOnlyList(Of String) Implements IStartupFileProvider.GetStartupFiles
        Return New List(Of String) From {ModConfigRelPath}
    End Function

    Public Function RenderStartupFile(relativePath As String,
                                      instanceConfig As InstanceConfig,
                                      existingText As String) As String Implements IStartupFileProvider.RenderStartupFile
        If Not String.Equals(relativePath, ModConfigRelPath, StringComparison.OrdinalIgnoreCase) Then
            Return Nothing
        End If

        Dim f = instanceConfig.CustomFields

        ' Round-trip: parse the current on-disk config and overlay only
        ' the fields this plugin owns. Unknown fields — settings a newer
        ' mod version added, or values tuned by hand that the schema
        ' doesn't expose — survive untouched.
        Dim root As JsonObject = Nothing
        If Not String.IsNullOrWhiteSpace(existingText) Then
            Try
                root = TryCast(JsonNode.Parse(existingText), JsonObject)
            Catch
                ' Corrupt/non-JSON file — regenerate from scratch.
            End Try
        End If
        If root Is Nothing Then root = New JsonObject()

        root("FarmName") = GetField(f, "FarmName", "Stardew")
        root("StartingCabins") = GetIntOr(f, "StartingCabins", 3)
        root("CabinLayout") = GetField(f, "CabinLayout", "separate")
        root("ProfitMargin") = GetField(f, "ProfitMargin", "normal")
        root("MoneyStyle") = GetField(f, "MoneyStyle", "shared")
        root("FarmType") = GetField(f, "FarmType", "standard")
        root("CommunityCenterBundles") = GetField(f, "CommunityCenterBundles", "normal")
        root("GuaranteeYear1Completable") = GetBoolOr(f, "GuaranteeYear1Completable", False)
        root("MineRewards") = GetField(f, "MineRewards", "normal")

        ' RandomSeed: numeric → number, anything else → null.
        Dim seedRaw = GetField(f, "RandomSeed", "")
        Dim seedVal As ULong
        If ULong.TryParse(seedRaw.Trim(), seedVal) Then
            root("RandomSeed") = seedVal
        Else
            root("RandomSeed") = Nothing
        End If

        root("PetBreed") = GetIntOr(f, "PetBreed", 0)
        root("PetName") = GetField(f, "PetName", "Stella")
        root("MushroomsOrBats") = GetField(f, "MushroomsOrBats", "Mushrooms")
        root("EnableCropSaver") = GetBoolOr(f, "EnableCropSaver", True)
        root("PurchaseJojaMembership") = GetBoolOr(f, "PurchaseJojaMembership", False)
        root("SpawnMonstersOnFarmAtNight") = GetBoolOr(f, "SpawnMonstersOnFarmAtNight", False)
        root("MoveBuildPermission") = GetField(f, "MoveBuildPermission", "off")
        root("TryActivatingInviteCode") = GetBoolOr(f, "TryActivatingInviteCode", True)
        root("UpgradeHouseLevelBasedOnFarmhand") = GetBoolOr(f, "UpgradeHouseLevelBasedOnFarmhand", False)

        ' Password: null when empty (matches upstream default).
        Dim pwRaw = GetField(f, "Password", "")
        If String.IsNullOrEmpty(pwRaw) Then
            root("Password") = Nothing
        Else
            root("Password") = pwRaw
        End If

        ' PasswordProtected: granularity not exposed in the schema —
        ' ensure the known gates exist (upstream all-true defaults) but
        ' don't overwrite ones already present, and preserve any gates a
        ' newer mod version added.
        Dim gatesNode = TryCast(root("PasswordProtected"), JsonObject)
        If gatesNode Is Nothing Then
            gatesNode = New JsonObject()
            root("PasswordProtected") = gatesNode
        End If
        Dim gates As String() = {"Pause", "Build", "Demolish", "LetMePlay", "TakeOver",
                                 "SafeInviteCode", "InviteCode", "ForceInviteCode",
                                 "Invisible", "Sleep", "ForceSleep", "ForceResetDay",
                                 "ForceShutdown", "Wallet", "SpawnMonster",
                                 "MoveBuildPermission", "UpgradeHouseLevelBasedOnFarmhand"}
        For Each gate In gates
            If Not gatesNode.ContainsKey(gate) Then
                gatesNode(gate) = True
            End If
        Next

        Return root.ToJsonString(New JsonSerializerOptions With {.WriteIndented = True})
    End Function

    Private Shared Function GetBoolOr(fields As Dictionary(Of String, String),
                                      key As String,
                                      fallback As Boolean) As Boolean
        Dim raw = GetField(fields, key, If(fallback, "true", "false"))
        Return IsTrue(raw)
    End Function

    Private Shared Function GetIntOr(fields As Dictionary(Of String, String),
                                     key As String,
                                     fallback As Integer) As Integer
        Dim raw = GetField(fields, key, "")
        Dim parsed As Integer
        If Integer.TryParse(raw.Trim(), parsed) Then Return parsed
        Return fallback
    End Function

    Public Function CreateModManager() As IModManager Implements IGamePlugin.CreateModManager
        Return Nothing
    End Function

    ' ------------------------------------------------------------
    '  Farm save migration — saves live OUTSIDE the install root
    '  (the game hardcodes the OS user profile:
    '  %APPDATA%\StardewValley\Saves on Windows — the node service
    '  account's systemprofile in practice — and
    '  ~/.config/StardewValley/Saves on Linux), so the file panel
    '  can't reach them directly. Instead: a 'backups' managed
    '  directory under the install root + archive/restore
    '  operations that tar the saves dir into/out of it. Migration =
    '  archive on node A → download → upload on node B → restore.
    '
    '  IMPORTANT: run these only while the instance is stopped —
    '  archiving mid-save copies a torn farm; restoring over a
    '  running host is worse. Not mechanically enforced (documented
    '  instead), matching the file panel's general model.
    ' ------------------------------------------------------------

    Private Const BackupsDirRef As String = "backups"

    Public Function GetManagedDirectories(config As InstanceConfig) As IReadOnlyList(Of ManagedDirectory) Implements IManagedDirectoriesProvider.GetManagedDirectories
        Return New List(Of ManagedDirectory) From {
            New ManagedDirectory With {
                .RelativePath = BackupsDirRef,
                .DisplayName = "Farm Backups",
                .Permissions = DirPermissions.Read Or DirPermissions.Write Or DirPermissions.Delete
            }
        }
    End Function

    Public Function GetTargetDirectoryRef() As String Implements IFileGenerationProvider.GetTargetDirectoryRef
        Return BackupsDirRef
    End Function

    Public Function GetButtonLabel() As String Implements IFileGenerationProvider.GetButtonLabel
        Return "Archive / Restore Saves..."
    End Function

    Public Function GetTabTitle() As String Implements IFileGenerationProvider.GetTabTitle
        Return "Farm Save Archive"
    End Function

    Public Function GetGenerationSchema(instanceConfig As InstanceConfig) As IReadOnlyList(Of ConfigFieldDescriptor) Implements IFileGenerationProvider.GetGenerationSchema
        Return New List(Of ConfigFieldDescriptor) From {
            New ConfigFieldDescriptor With {
                .Key = "StopNotice",
                .Label = "Stop the instance first",
                .Description = "Archiving while the server is saving produces a corrupt copy, and restoring over a running server corrupts the live farm. Make sure the instance is STOPPED before running either action.",
                .FieldType = ConfigFieldType.Notice
            },
            New ConfigFieldDescriptor With {
                .Key = "Action",
                .Label = "Action",
                .Description = "Archive packs the node's entire Stardew saves folder into an archive under Farm Backups (download it from there). Restore unpacks a previously uploaded archive back into the saves folder, overwriting matching farms.",
                .FieldType = ConfigFieldType.[Enum],
                .EnumValues = New List(Of String) From {"Archive", "Restore"},
                .DefaultValue = "Archive"
            },
            New ConfigFieldDescriptor With {
                .Key = "ArchiveName",
                .Label = "Archive name (Archive action)",
                .Description = "Base file name for the new archive; extension is added automatically (.zip on Windows nodes, .tar.gz on Linux).",
                .FieldType = ConfigFieldType.Text,
                .DefaultValue = "farm-saves"
            },
            New ConfigFieldDescriptor With {
                .Key = "FarmFolder",
                .Label = "Farm folder (Archive action)",
                .Description = "Exact save-folder name to archive, e.g. Stardew_443337687 (see the SMAPI log's 'loaded save' line). Leave EMPTY to archive every farm on the node.",
                .FieldType = ConfigFieldType.Text
            },
            New ConfigFieldDescriptor With {
                .Key = "SourceArchive",
                .Label = "Source archive (Restore action)",
                .Description = "Archive in Farm Backups to restore from.",
                .FieldType = ConfigFieldType.ManagedFilePicker,
                .ManagedDirectoryRef = BackupsDirRef
            }
        }
    End Function

    Public Function BuildGenerationSteps(values As Dictionary(Of String, String),
                                         instanceConfig As InstanceConfig) As GenerationStepBundle Implements IFileGenerationProvider.BuildGenerationSteps
        ' All paths are RELATIVE to the install root — the node runs
        ' generation steps with the install directory as the working
        ' directory, and InstanceConfig.WorkingDirectory isn't
        ' populated in the generation-panel context anyway.
        Dim isLinux = (instanceConfig.Platform = NodePlatform.Linux)
        Dim action = GetField(values, "Action", "Archive")

        Dim bundle As New GenerationStepBundle With {
            .Steps = New List(Of InstallStep),
            .TimeoutSeconds = 600
        }

        If String.Equals(action, "Restore", StringComparison.OrdinalIgnoreCase) Then
            Dim src = GetField(values, "SourceArchive", "")
            If String.IsNullOrWhiteSpace(src) Then
                Throw New ArgumentException("Pick a source archive for the Restore action.")
            End If
            ' Picker may hand back "backups/x.zip" or bare "x.zip" —
            ' normalise to the file name and rebuild the full path.
            Dim srcName = src.Replace("\"c, "/"c)
            Dim slash = srcName.LastIndexOf("/"c)
            If slash >= 0 Then srcName = srcName.Substring(slash + 1)
            If srcName.Contains("..") Then
                Throw New ArgumentException("Invalid archive name.")
            End If

            If isLinux Then
                Dim archiveRel = BackupsDirRef & "/" & srcName
                ' Archives can arrive from either platform (migration!):
                ' Windows archives are .zip, Linux ones .tar.gz. GNU tar
                ' can't read zip, so branch by extension. `tar -xf`
                ' auto-detects gzip/xz for the tar family; `unzip -o`
                ' handles zip (present on stock Ubuntu/Debian servers).
                Dim extractCmd As String
                If srcName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) Then
                    ' Prefer unzip, fall back to python3's zipfile module
                    ' (stock on Ubuntu server even when unzip isn't).
                    extractCmd = "if command -v unzip >/dev/null 2>&1; then unzip -o '" & archiveRel & "' -d \""$HOME/.config/StardewValley/Saves\""; else python3 -m zipfile -e '" & archiveRel & "' \""$HOME/.config/StardewValley/Saves\""; fi"
                Else
                    extractCmd = "tar -xf '" & archiveRel & "' -C \""$HOME/.config/StardewValley/Saves\"""
                End If
                bundle.Steps.Add(New RunProcessStep With {
                    .StepName = "Restore farm saves",
                    .ExecutablePath = "/bin/sh",
                    .Arguments = "-c ""mkdir -p \""$HOME/.config/StardewValley/Saves\"" && " & extractCmd & """",
                    .TimeoutMs = 300000
                })
            Else
                Dim archiveRel = BackupsDirRef & "\" & srcName
                ' Same cross-platform concern in reverse: Windows'
                ' bundled bsdtar reads BOTH zip and tar.gz, so a
                ' single `tar -x -f` covers Linux-made archives too.
                bundle.Steps.Add(New RunProcessStep With {
                    .StepName = "Restore farm saves",
                    .ExecutablePath = "C:\Windows\System32\cmd.exe",
                    .Arguments = "/c (if not exist ""%APPDATA%\StardewValley\Saves"" mkdir ""%APPDATA%\StardewValley\Saves"") && tar -x -f """ & archiveRel & """ -C ""%APPDATA%\StardewValley\Saves""",
                    .TimeoutMs = 300000
                })
            End If
            ' No ExpectedOutputRelativePath — restore produces no file
            ' under the install root; step exit code is the signal.
            Return bundle
        End If

        ' Archive.
        Dim baseName = GetField(values, "ArchiveName", "farm-saves").Trim()
        If baseName.Length = 0 Then baseName = "farm-saves"
        If baseName.Contains("..") OrElse baseName.IndexOfAny("/\""".ToCharArray()) >= 0 Then
            Throw New ArgumentException("Archive name must be a plain file name (no slashes or quotes).")
        End If

        ' Scope: one farm folder, or everything when the field is empty.
        ' tar's trailing member argument selects the subdir; extraction
        ' recreates it under Saves, so single-farm archives restore
        ' single farms with the unchanged Restore action.
        Dim farmFolder = GetField(values, "FarmFolder", "").Trim()
        If farmFolder.Contains("..") OrElse farmFolder.IndexOfAny("/\""'".ToCharArray()) >= 0 Then
            Throw New ArgumentException("Farm folder must be a plain folder name (no slashes or quotes).")
        End If
        Dim tarMember = If(farmFolder.Length = 0, ".", farmFolder)

        If isLinux Then
            Dim outRel = BackupsDirRef & "/" & baseName & ".tar.gz"
            bundle.Steps.Add(New RunProcessStep With {
                .StepName = "Archive farm saves",
                .ExecutablePath = "/bin/sh",
                .Arguments = "-c ""mkdir -p '" & BackupsDirRef & "' && tar -czf '" & outRel & "' -C \""$HOME/.config/StardewValley/Saves\"" '" & tarMember & "'""",
                .TimeoutMs = 300000
            })
            bundle.ExpectedOutputRelativePath = outRel
        Else
            Dim outRel = BackupsDirRef & "\" & baseName & ".zip"
            bundle.Steps.Add(New RunProcessStep With {
                .StepName = "Archive farm saves",
                .ExecutablePath = "C:\Windows\System32\cmd.exe",
                .Arguments = "/c (if not exist """ & BackupsDirRef & """ mkdir """ & BackupsDirRef & """) && tar -a -c -f """ & outRel & """ -C ""%APPDATA%\StardewValley\Saves"" """ & tarMember & """",
                .TimeoutMs = 300000
            })
            bundle.ExpectedOutputRelativePath = BackupsDirRef & "/" & baseName & ".zip"
        End If

        Return bundle
    End Function

    ' ------------------------------------------------------------
    '  Helpers
    ' ------------------------------------------------------------

    Private Shared Function IsTrue(value As String) As Boolean
        Return String.Equals(value, "true", StringComparison.OrdinalIgnoreCase) OrElse
               String.Equals(value, "1", StringComparison.Ordinal) OrElse
               String.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)
    End Function

    Private Shared Function GetField(fields As Dictionary(Of String, String),
                                     key As String,
                                     fallback As String) As String
        If fields Is Nothing Then Return fallback
        Dim value As String = Nothing
        If fields.TryGetValue(key, value) AndAlso Not String.IsNullOrWhiteSpace(value) Then
            Return value
        End If
        Return fallback
    End Function
End Class

Public Class StardewLogParser
    Implements ILogParser

    ' Anchored on the fork's [PGSM] lines (siteml/SMAPIDedicatedServerMod).
    ' Named groups via concat per project convention.
    Private Shared ReadOnly JoinRegex As New Text.RegularExpressions.Regex(
        "\[PGSM\] JOIN name=""(?<" & "N" & ">[^""]*)"" id=""(?<" & "I" & ">-?\d+)""",
        Text.RegularExpressions.RegexOptions.Compiled)
    Private Shared ReadOnly LeaveRegex As New Text.RegularExpressions.Regex(
        "\[PGSM\] LEAVE name=""(?<" & "N" & ">[^""]*)"" id=""(?<" & "I" & ">-?\d+)""",
        Text.RegularExpressions.RegexOptions.Compiled)
    Private Shared ReadOnly ReadyRegex As New Text.RegularExpressions.Regex(
        "\[PGSM\] READY farm=""(?<" & "F" & ">[^""]*)""",
        Text.RegularExpressions.RegexOptions.Compiled)

    Public ReadOnly Property GameId As String Implements ILogParser.GameId
        Get
            Return "stardewvalley"
        End Get
    End Property

    ' No cross-instance identity concept (one farm per instance);
    ' downstream falls back to "{gameId}:{instanceId}".
    Public ReadOnly Property CurrentSessionIdentity As String Implements ILogParser.CurrentSessionIdentity
        Get
            Return Nothing
        End Get
    End Property

    Public Function ParseLine(line As LogLine) As ParsedLogEvent Implements ILogParser.ParseLine
        If line Is Nothing OrElse String.IsNullOrEmpty(line.Text) Then
            Return ParsedLogEvent.NoMatch
        End If
        Dim text = line.Text

        ' Cheap prefilter — every interesting line carries the tag.
        If text.IndexOf("[PGSM]", StringComparison.Ordinal) < 0 Then
            Return ParsedLogEvent.NoMatch
        End If

        Dim m = JoinRegex.Match(text)
        If m.Success Then
            Return New ParsedLogEvent With {
                .EventType = LogEventType.PlayerJoin,
                .PlayerInfo = New PlayerInfo With {
                    .PlayerName = m.Groups("N").Value,
                    .PlayerId = m.Groups("I").Value,
                    .JoinedAt = line.Timestamp
                }
            }
        End If

        m = LeaveRegex.Match(text)
        If m.Success Then
            Return New ParsedLogEvent With {
                .EventType = LogEventType.PlayerLeave,
                .PlayerInfo = New PlayerInfo With {
                    .PlayerName = m.Groups("N").Value,
                    .PlayerId = m.Groups("I").Value
                }
            }
        End If

        m = ReadyRegex.Match(text)
        If m.Success Then
            Return New ParsedLogEvent With {
                .EventType = LogEventType.ServerReady,
                .Message = "Hosting farm " & m.Groups("F").Value
            }
        End If

        Return ParsedLogEvent.NoMatch
    End Function

    Public Function GetCrashPatterns() As IReadOnlyList(Of String) Implements ILogParser.GetCrashPatterns
        Return New List(Of String)
    End Function
End Class
