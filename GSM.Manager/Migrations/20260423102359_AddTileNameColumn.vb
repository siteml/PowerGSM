Imports Microsoft.EntityFrameworkCore.Migrations
Imports Microsoft.VisualBasic

Namespace Global.Migrations
    ''' <inheritdoc />
    Partial Public Class AddTileNameColumn
        Inherits Migration

        ''' <inheritdoc />
        Protected Overrides Sub Up(migrationBuilder As MigrationBuilder)
            migrationBuilder.AddColumn(Of String)(
                name:="TileName",
                table:="SessionHosts",
                type:="TEXT",
                maxLength:=200,
                nullable:=True)
        End Sub

        ''' <inheritdoc />
        Protected Overrides Sub Down(migrationBuilder As MigrationBuilder)
            migrationBuilder.DropColumn(
                name:="TileName",
                table:="SessionHosts")
        End Sub
    End Class
End Namespace
