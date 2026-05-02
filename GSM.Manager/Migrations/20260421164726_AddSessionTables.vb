Imports System
Imports Microsoft.EntityFrameworkCore.Migrations
Imports Microsoft.VisualBasic

Namespace Global.Migrations
    ''' <inheritdoc />
    Partial Public Class AddSessionTables
        Inherits Migration

        ''' <inheritdoc />
        Protected Overrides Sub Up(migrationBuilder As MigrationBuilder)
            migrationBuilder.CreateTable(
                name:="AppSettings",
                columns:=Function(table) New With {
                    .SettingKey = table.Column(Of String)(type:="TEXT", maxLength:=100, nullable:=False),
                    .Value = table.Column(Of String)(type:="TEXT", maxLength:=4000, nullable:=True)
                },
                constraints:=Sub(table)
                    table.PrimaryKey("PK_AppSettings", Function(x) x.SettingKey)
                End Sub)

            migrationBuilder.CreateTable(
                name:="AutomationRules",
                columns:=Function(table) New With {
                    .RuleId = table.Column(Of String)(type:="TEXT", nullable:=False),
                    .RuleName = table.Column(Of String)(type:="TEXT", maxLength:=200, nullable:=False),
                    .IsEnabled = table.Column(Of Boolean)(type:="INTEGER", nullable:=False),
                    .ScopeKind = table.Column(Of String)(type:="TEXT", nullable:=True),
                    .TargetId = table.Column(Of String)(type:="TEXT", nullable:=True),
                    .TriggerJson = table.Column(Of String)(type:="TEXT", nullable:=True),
                    .ConditionsJson = table.Column(Of String)(type:="TEXT", nullable:=True),
                    .ActionJson = table.Column(Of String)(type:="TEXT", nullable:=True),
                    .CreatedUtc = table.Column(Of Date)(type:="TEXT", nullable:=False),
                    .UpdatedUtc = table.Column(Of Date)(type:="TEXT", nullable:=False)
                },
                constraints:=Sub(table)
                    table.PrimaryKey("PK_AutomationRules", Function(x) x.RuleId)
                End Sub)

            migrationBuilder.CreateTable(
                name:="ChatMessages",
                columns:=Function(table) New With {
                    .MessageId = table.Column(Of String)(type:="TEXT", nullable:=False),
                    .SessionIdentity = table.Column(Of String)(type:="TEXT", maxLength:=200, nullable:=False),
                    .NodeId = table.Column(Of String)(type:="TEXT", maxLength:=100, nullable:=True),
                    .InstanceId = table.Column(Of String)(type:="TEXT", maxLength:=100, nullable:=True),
                    .TimestampUtc = table.Column(Of Date)(type:="TEXT", nullable:=False),
                    .PlayerName = table.Column(Of String)(type:="TEXT", maxLength:=100, nullable:=True),
                    .Text = table.Column(Of String)(type:="TEXT", maxLength:=4000, nullable:=True)
                },
                constraints:=Sub(table)
                    table.PrimaryKey("PK_ChatMessages", Function(x) x.MessageId)
                End Sub)

            migrationBuilder.CreateTable(
                name:="Nodes",
                columns:=Function(table) New With {
                    .NodeId = table.Column(Of String)(type:="TEXT", nullable:=False),
                    .DisplayName = table.Column(Of String)(type:="TEXT", maxLength:=200, nullable:=False),
                    .HostAddress = table.Column(Of String)(type:="TEXT", maxLength:=500, nullable:=False),
                    .Port = table.Column(Of Integer)(type:="INTEGER", nullable:=False),
                    .AuthToken = table.Column(Of String)(type:="TEXT", nullable:=True),
                    .IsEnabled = table.Column(Of Boolean)(type:="INTEGER", nullable:=False),
                    .LastSeenUtc = table.Column(Of Date)(type:="TEXT", nullable:=False),
                    .OsDescription = table.Column(Of String)(type:="TEXT", nullable:=True)
                },
                constraints:=Sub(table)
                    table.PrimaryKey("PK_Nodes", Function(x) x.NodeId)
                End Sub)

            migrationBuilder.CreateTable(
                name:="NotificationDestinations",
                columns:=Function(table) New With {
                    .DestinationId = table.Column(Of String)(type:="TEXT", nullable:=False),
                    .DisplayName = table.Column(Of String)(type:="TEXT", maxLength:=100, nullable:=False),
                    .Enabled = table.Column(Of Boolean)(type:="INTEGER", nullable:=False),
                    .TransportKind = table.Column(Of String)(type:="TEXT", maxLength:=40, nullable:=False),
                    .TransportConfigJson = table.Column(Of String)(type:="TEXT", nullable:=True),
                    .EnabledEventTypesJson = table.Column(Of String)(type:="TEXT", nullable:=True),
                    .InstallationFilterJson = table.Column(Of String)(type:="TEXT", nullable:=True),
                    .InstanceFilterJson = table.Column(Of String)(type:="TEXT", nullable:=True),
                    .VisibilityProfileId = table.Column(Of String)(type:="TEXT", nullable:=True),
                    .TemplateOverridesJson = table.Column(Of String)(type:="TEXT", nullable:=True),
                    .CreatedUtc = table.Column(Of Date)(type:="TEXT", nullable:=False),
                    .UpdatedUtc = table.Column(Of Date)(type:="TEXT", nullable:=False)
                },
                constraints:=Sub(table)
                    table.PrimaryKey("PK_NotificationDestinations", Function(x) x.DestinationId)
                End Sub)

            migrationBuilder.CreateTable(
                name:="NotificationPlugins",
                columns:=Function(table) New With {
                    .PluginId = table.Column(Of String)(type:="TEXT", nullable:=False),
                    .DisplayName = table.Column(Of String)(type:="TEXT", maxLength:=200, nullable:=False),
                    .IsEnabled = table.Column(Of Boolean)(type:="INTEGER", nullable:=False),
                    .ConfigJson = table.Column(Of String)(type:="TEXT", nullable:=True)
                },
                constraints:=Sub(table)
                    table.PrimaryKey("PK_NotificationPlugins", Function(x) x.PluginId)
                End Sub)

            migrationBuilder.CreateTable(
                name:="NotificationSubscriptions",
                columns:=Function(table) New With {
                    .SubscriptionId = table.Column(Of String)(type:="TEXT", nullable:=False),
                    .PluginId = table.Column(Of String)(type:="TEXT", maxLength:=100, nullable:=False),
                    .EventName = table.Column(Of String)(type:="TEXT", nullable:=True),
                    .ScopeKind = table.Column(Of String)(type:="TEXT", nullable:=True),
                    .TargetId = table.Column(Of String)(type:="TEXT", nullable:=True),
                    .RoutingHintsJson = table.Column(Of String)(type:="TEXT", nullable:=True),
                    .IsEnabled = table.Column(Of Boolean)(type:="INTEGER", nullable:=False)
                },
                constraints:=Sub(table)
                    table.PrimaryKey("PK_NotificationSubscriptions", Function(x) x.SubscriptionId)
                End Sub)

            migrationBuilder.CreateTable(
                name:="PlayerSessions",
                columns:=Function(table) New With {
                    .PlayerSessionId = table.Column(Of String)(type:="TEXT", nullable:=False),
                    .SessionIdentity = table.Column(Of String)(type:="TEXT", maxLength:=200, nullable:=False),
                    .PlayerName = table.Column(Of String)(type:="TEXT", maxLength:=100, nullable:=False),
                    .FirstSeenUtc = table.Column(Of Date)(type:="TEXT", nullable:=False),
                    .LastSeenUtc = table.Column(Of Date)(type:="TEXT", nullable:=False),
                    .LastHostInstanceId = table.Column(Of String)(type:="TEXT", maxLength:=100, nullable:=True)
                },
                constraints:=Sub(table)
                    table.PrimaryKey("PK_PlayerSessions", Function(x) x.PlayerSessionId)
                End Sub)

            migrationBuilder.CreateTable(
                name:="RealmCredentials",
                columns:=Function(table) New With {
                    .CredentialId = table.Column(Of String)(type:="TEXT", nullable:=False),
                    .DisplayName = table.Column(Of String)(type:="TEXT", nullable:=True),
                    .GameId = table.Column(Of String)(type:="TEXT", maxLength:=100, nullable:=False),
                    .EncryptedCustomerKey = table.Column(Of Byte())(type:="BLOB", nullable:=True),
                    .EncryptedProviderKey = table.Column(Of Byte())(type:="BLOB", nullable:=True)
                },
                constraints:=Sub(table)
                    table.PrimaryKey("PK_RealmCredentials", Function(x) x.CredentialId)
                End Sub)

            migrationBuilder.CreateTable(
                name:="RuleExecutions",
                columns:=Function(table) New With {
                    .ExecutionId = table.Column(Of String)(type:="TEXT", nullable:=False),
                    .RuleId = table.Column(Of String)(type:="TEXT", maxLength:=100, nullable:=False),
                    .StartedAtUtc = table.Column(Of Date)(type:="TEXT", nullable:=False),
                    .CompletedAtUtc = table.Column(Of Date)(type:="TEXT", nullable:=True),
                    .TriggerReason = table.Column(Of String)(type:="TEXT", nullable:=True),
                    .ConditionResultsJson = table.Column(Of String)(type:="TEXT", nullable:=True),
                    .ActionResultJson = table.Column(Of String)(type:="TEXT", nullable:=True),
                    .WasSkipped = table.Column(Of Boolean)(type:="INTEGER", nullable:=False),
                    .SkipReason = table.Column(Of String)(type:="TEXT", nullable:=True)
                },
                constraints:=Sub(table)
                    table.PrimaryKey("PK_RuleExecutions", Function(x) x.ExecutionId)
                End Sub)

            migrationBuilder.CreateTable(
                name:="SessionHosts",
                columns:=Function(table) New With {
                    .HostId = table.Column(Of String)(type:="TEXT", nullable:=False),
                    .SessionIdentity = table.Column(Of String)(type:="TEXT", maxLength:=200, nullable:=False),
                    .InstanceId = table.Column(Of String)(type:="TEXT", maxLength:=100, nullable:=False),
                    .HostedFromUtc = table.Column(Of Date)(type:="TEXT", nullable:=False),
                    .HostedUntilUtc = table.Column(Of Date)(type:="TEXT", nullable:=True)
                },
                constraints:=Sub(table)
                    table.PrimaryKey("PK_SessionHosts", Function(x) x.HostId)
                End Sub)

            migrationBuilder.CreateTable(
                name:="SteamCredentials",
                columns:=Function(table) New With {
                    .CredentialId = table.Column(Of String)(type:="TEXT", nullable:=False),
                    .DisplayName = table.Column(Of String)(type:="TEXT", nullable:=True),
                    .Username = table.Column(Of String)(type:="TEXT", maxLength:=200, nullable:=False),
                    .EncryptedPassword = table.Column(Of Byte())(type:="BLOB", nullable:=True),
                    .IsAnonymous = table.Column(Of Boolean)(type:="INTEGER", nullable:=False)
                },
                constraints:=Sub(table)
                    table.PrimaryKey("PK_SteamCredentials", Function(x) x.CredentialId)
                End Sub)

            migrationBuilder.CreateTable(
                name:="VisibilityProfiles",
                columns:=Function(table) New With {
                    .ProfileId = table.Column(Of String)(type:="TEXT", nullable:=False),
                    .DisplayName = table.Column(Of String)(type:="TEXT", maxLength:=100, nullable:=False),
                    .AllowedFieldsJson = table.Column(Of String)(type:="TEXT", nullable:=True),
                    .IsBuiltIn = table.Column(Of Boolean)(type:="INTEGER", nullable:=False),
                    .CreatedUtc = table.Column(Of Date)(type:="TEXT", nullable:=False),
                    .UpdatedUtc = table.Column(Of Date)(type:="TEXT", nullable:=False)
                },
                constraints:=Sub(table)
                    table.PrimaryKey("PK_VisibilityProfiles", Function(x) x.ProfileId)
                End Sub)

            migrationBuilder.CreateTable(
                name:="Installations",
                columns:=Function(table) New With {
                    .InstallationId = table.Column(Of String)(type:="TEXT", nullable:=False),
                    .GameId = table.Column(Of String)(type:="TEXT", maxLength:=100, nullable:=False),
                    .DisplayName = table.Column(Of String)(type:="TEXT", nullable:=True),
                    .NodeId = table.Column(Of String)(type:="TEXT", nullable:=True),
                    .InstallPath = table.Column(Of String)(type:="TEXT", maxLength:=1000, nullable:=False),
                    .InstallMethod = table.Column(Of String)(type:="TEXT", nullable:=True),
                    .InstalledVersion = table.Column(Of String)(type:="TEXT", nullable:=True),
                    .SteamCredentialId = table.Column(Of String)(type:="TEXT", nullable:=True),
                    .ConfigJson = table.Column(Of String)(type:="TEXT", nullable:=True),
                    .CreatedUtc = table.Column(Of Date)(type:="TEXT", nullable:=False),
                    .UpdatedUtc = table.Column(Of Date)(type:="TEXT", nullable:=False),
                    .RunCommonRedist = table.Column(Of Boolean)(type:="INTEGER", nullable:=False)
                },
                constraints:=Sub(table)
                    table.PrimaryKey("PK_Installations", Function(x) x.InstallationId)
                    table.ForeignKey(
                        name:="FK_Installations_Nodes_NodeId",
                        column:=Function(x) x.NodeId,
                        principalTable:="Nodes",
                        principalColumn:="NodeId")
                End Sub)

            migrationBuilder.CreateTable(
                name:="Instances",
                columns:=Function(table) New With {
                    .InstanceId = table.Column(Of String)(type:="TEXT", nullable:=False),
                    .InstallationId = table.Column(Of String)(type:="TEXT", nullable:=True),
                    .DisplayName = table.Column(Of String)(type:="TEXT", nullable:=True),
                    .GameId = table.Column(Of String)(type:="TEXT", maxLength:=100, nullable:=False),
                    .ConfigJson = table.Column(Of String)(type:="TEXT", nullable:=True),
                    .ExeOverride = table.Column(Of String)(type:="TEXT", nullable:=True),
                    .AutoStart = table.Column(Of Boolean)(type:="INTEGER", nullable:=False),
                    .CreatedUtc = table.Column(Of Date)(type:="TEXT", nullable:=False),
                    .UpdatedUtc = table.Column(Of Date)(type:="TEXT", nullable:=False)
                },
                constraints:=Sub(table)
                    table.PrimaryKey("PK_Instances", Function(x) x.InstanceId)
                    table.ForeignKey(
                        name:="FK_Instances_Installations_InstallationId",
                        column:=Function(x) x.InstallationId,
                        principalTable:="Installations",
                        principalColumn:="InstallationId")
                End Sub)

            migrationBuilder.CreateIndex(
                name:="IX_ChatMessages_SessionIdentity_TimestampUtc",
                table:="ChatMessages",
                columns:={"SessionIdentity", "TimestampUtc"})

            migrationBuilder.CreateIndex(
                name:="IX_ChatMessages_TimestampUtc",
                table:="ChatMessages",
                column:="TimestampUtc")

            migrationBuilder.CreateIndex(
                name:="IX_Installations_NodeId",
                table:="Installations",
                column:="NodeId")

            migrationBuilder.CreateIndex(
                name:="IX_Instances_InstallationId",
                table:="Instances",
                column:="InstallationId")

            migrationBuilder.CreateIndex(
                name:="IX_NotificationDestinations_Enabled",
                table:="NotificationDestinations",
                column:="Enabled")

            migrationBuilder.CreateIndex(
                name:="IX_PlayerSessions_SessionIdentity_LastSeenUtc",
                table:="PlayerSessions",
                columns:={"SessionIdentity", "LastSeenUtc"})

            migrationBuilder.CreateIndex(
                name:="IX_PlayerSessions_SessionIdentity_PlayerName",
                table:="PlayerSessions",
                columns:={"SessionIdentity", "PlayerName"},
                unique:=True)

            migrationBuilder.CreateIndex(
                name:="IX_RuleExecutions_RuleId",
                table:="RuleExecutions",
                column:="RuleId")

            migrationBuilder.CreateIndex(
                name:="IX_RuleExecutions_StartedAtUtc",
                table:="RuleExecutions",
                column:="StartedAtUtc")

            migrationBuilder.CreateIndex(
                name:="IX_SessionHosts_InstanceId_HostedUntilUtc",
                table:="SessionHosts",
                columns:={"InstanceId", "HostedUntilUtc"})

            migrationBuilder.CreateIndex(
                name:="IX_SessionHosts_SessionIdentity_HostedFromUtc",
                table:="SessionHosts",
                columns:={"SessionIdentity", "HostedFromUtc"})
        End Sub

        ''' <inheritdoc />
        Protected Overrides Sub Down(migrationBuilder As MigrationBuilder)
            migrationBuilder.DropTable(
                name:="AppSettings")

            migrationBuilder.DropTable(
                name:="AutomationRules")

            migrationBuilder.DropTable(
                name:="ChatMessages")

            migrationBuilder.DropTable(
                name:="Instances")

            migrationBuilder.DropTable(
                name:="NotificationDestinations")

            migrationBuilder.DropTable(
                name:="NotificationPlugins")

            migrationBuilder.DropTable(
                name:="NotificationSubscriptions")

            migrationBuilder.DropTable(
                name:="PlayerSessions")

            migrationBuilder.DropTable(
                name:="RealmCredentials")

            migrationBuilder.DropTable(
                name:="RuleExecutions")

            migrationBuilder.DropTable(
                name:="SessionHosts")

            migrationBuilder.DropTable(
                name:="SteamCredentials")

            migrationBuilder.DropTable(
                name:="VisibilityProfiles")

            migrationBuilder.DropTable(
                name:="Installations")

            migrationBuilder.DropTable(
                name:="Nodes")
        End Sub
    End Class
End Namespace
