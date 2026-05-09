Imports Microsoft.EntityFrameworkCore.Migrations
Imports Microsoft.VisualBasic

Namespace Global.Migrations
    ''' <inheritdoc />
    Partial Public Class AddPanelLayoutAndGrouping
        Inherits Migration

        ''' <inheritdoc />
        Protected Overrides Sub Up(migrationBuilder As MigrationBuilder)
            migrationBuilder.AddColumn(Of String)(
                name:="GroupingKind",
                table:="DiscordPanels",
                type:="TEXT",
                maxLength:=40,
                nullable:=False,
                defaultValue:="")

            migrationBuilder.AddColumn(Of String)(
                name:="LayoutJson",
                table:="DiscordPanels",
                type:="TEXT",
                nullable:=True)
        End Sub

        ''' <inheritdoc />
        Protected Overrides Sub Down(migrationBuilder As MigrationBuilder)
            migrationBuilder.DropColumn(
                name:="GroupingKind",
                table:="DiscordPanels")

            migrationBuilder.DropColumn(
                name:="LayoutJson",
                table:="DiscordPanels")
        End Sub
    End Class
End Namespace
