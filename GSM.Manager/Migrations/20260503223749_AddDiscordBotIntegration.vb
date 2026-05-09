Imports System
Imports Microsoft.EntityFrameworkCore.Migrations
Imports Microsoft.VisualBasic

Namespace Global.Migrations
    ''' <inheritdoc />
    Partial Public Class AddDiscordBotIntegration
        Inherits Migration

        ''' <inheritdoc />
        Protected Overrides Sub Up(migrationBuilder As MigrationBuilder)
            migrationBuilder.CreateTable(
                name:="DiscordBotConfigs",
                columns:=Function(table) New With {
                    .ConfigId = table.Column(Of String)(type:="TEXT", maxLength:=50, nullable:=False),
                    .DisplayName = table.Column(Of String)(type:="TEXT", maxLength:=100, nullable:=False),
                    .EncryptedToken = table.Column(Of Byte())(type:="BLOB", nullable:=True),
                    .Enabled = table.Column(Of Boolean)(type:="INTEGER", nullable:=False),
                    .CreatedUtc = table.Column(Of Date)(type:="TEXT", nullable:=False),
                    .UpdatedUtc = table.Column(Of Date)(type:="TEXT", nullable:=False)
                },
                constraints:=Sub(table)
                    table.PrimaryKey("PK_DiscordBotConfigs", Function(x) x.ConfigId)
                End Sub)

            migrationBuilder.CreateTable(
                name:="DiscordPanels",
                columns:=Function(table) New With {
                    .PanelId = table.Column(Of String)(type:="TEXT", maxLength:=50, nullable:=False),
                    .GuildId = table.Column(Of String)(type:="TEXT", maxLength:=50, nullable:=False),
                    .ChannelId = table.Column(Of String)(type:="TEXT", maxLength:=50, nullable:=False),
                    .MessageId = table.Column(Of String)(type:="TEXT", maxLength:=50, nullable:=True),
                    .DisplayName = table.Column(Of String)(type:="TEXT", maxLength:=100, nullable:=False),
                    .ScopeKind = table.Column(Of String)(type:="TEXT", maxLength:=40, nullable:=False),
                    .ScopeTargetId = table.Column(Of String)(type:="TEXT", maxLength:=200, nullable:=True),
                    .RefreshIntervalSeconds = table.Column(Of Integer)(type:="INTEGER", nullable:=False),
                    .CreatedUtc = table.Column(Of Date)(type:="TEXT", nullable:=False),
                    .UpdatedUtc = table.Column(Of Date)(type:="TEXT", nullable:=False)
                },
                constraints:=Sub(table)
                    table.PrimaryKey("PK_DiscordPanels", Function(x) x.PanelId)
                End Sub)

            migrationBuilder.CreateIndex(
                name:="IX_DiscordPanels_GuildId",
                table:="DiscordPanels",
                column:="GuildId")
        End Sub

        ''' <inheritdoc />
        Protected Overrides Sub Down(migrationBuilder As MigrationBuilder)
            migrationBuilder.DropTable(
                name:="DiscordBotConfigs")

            migrationBuilder.DropTable(
                name:="DiscordPanels")
        End Sub
    End Class
End Namespace
