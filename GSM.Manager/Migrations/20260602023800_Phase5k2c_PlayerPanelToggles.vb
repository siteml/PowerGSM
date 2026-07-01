Imports Microsoft.EntityFrameworkCore.Migrations
Imports Microsoft.VisualBasic

Namespace Global.Migrations
    ''' <inheritdoc />
    Partial Public Class Phase5k2c_PlayerPanelToggles
        Inherits Migration

        ''' <inheritdoc />
        Protected Overrides Sub Up(migrationBuilder As MigrationBuilder)
            migrationBuilder.AddColumn(Of Boolean)(
                name:="ShowJoinTime",
                table:="DiscordPanels",
                type:="INTEGER",
                nullable:=False,
                defaultValue:=False)

            migrationBuilder.AddColumn(Of Boolean)(
                name:="ShowTotalInTitle",
                table:="DiscordPanels",
                type:="INTEGER",
                nullable:=False,
                defaultValue:=False)
        End Sub

        ''' <inheritdoc />
        Protected Overrides Sub Down(migrationBuilder As MigrationBuilder)
            migrationBuilder.DropColumn(
                name:="ShowJoinTime",
                table:="DiscordPanels")

            migrationBuilder.DropColumn(
                name:="ShowTotalInTitle",
                table:="DiscordPanels")
        End Sub
    End Class
End Namespace
