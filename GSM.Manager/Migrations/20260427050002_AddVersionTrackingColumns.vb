Imports System
Imports Microsoft.EntityFrameworkCore.Migrations
Imports Microsoft.VisualBasic

Namespace Global.Migrations
    ''' <inheritdoc />
    Partial Public Class AddVersionTrackingColumns
        Inherits Migration

        ''' <inheritdoc />
        Protected Overrides Sub Up(migrationBuilder As MigrationBuilder)
            migrationBuilder.AddColumn(Of Date)(
                name:="LastVersionCheckUtc",
                table:="Installations",
                type:="TEXT",
                nullable:=True)

            migrationBuilder.AddColumn(Of String)(
                name:="LatestKnownVersion",
                table:="Installations",
                type:="TEXT",
                nullable:=True)
        End Sub

        ''' <inheritdoc />
        Protected Overrides Sub Down(migrationBuilder As MigrationBuilder)
            migrationBuilder.DropColumn(
                name:="LastVersionCheckUtc",
                table:="Installations")

            migrationBuilder.DropColumn(
                name:="LatestKnownVersion",
                table:="Installations")
        End Sub
    End Class
End Namespace
