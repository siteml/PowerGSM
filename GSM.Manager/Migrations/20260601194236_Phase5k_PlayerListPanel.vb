Imports Microsoft.EntityFrameworkCore.Migrations
Imports Microsoft.VisualBasic

Namespace Global.Migrations
    ''' <inheritdoc />
    Partial Public Class Phase5k_PlayerListPanel
        Inherits Migration

        ''' <inheritdoc />
        Protected Overrides Sub Up(migrationBuilder As MigrationBuilder)
            migrationBuilder.AddColumn(Of String)(
                name:="PanelKind",
                table:="DiscordPanels",
                type:="TEXT",
                maxLength:=40,
                nullable:=False,
                defaultValue:="InstanceManager")

            migrationBuilder.AddColumn(Of Boolean)(
                name:="ShowEmptyGroups",
                table:="DiscordPanels",
                type:="INTEGER",
                nullable:=False,
                defaultValue:=False)
        End Sub

        ''' <inheritdoc />
        Protected Overrides Sub Down(migrationBuilder As MigrationBuilder)
            migrationBuilder.DropColumn(
                name:="PanelKind",
                table:="DiscordPanels")

            migrationBuilder.DropColumn(
                name:="ShowEmptyGroups",
                table:="DiscordPanels")
        End Sub
    End Class
End Namespace
