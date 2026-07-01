Imports Microsoft.EntityFrameworkCore.Migrations
Imports Microsoft.VisualBasic

Namespace Global.Migrations
    ''' <inheritdoc />
    Partial Public Class NotificationScopeDimensions
        Inherits Migration

        ''' <inheritdoc />
        Protected Overrides Sub Up(migrationBuilder As MigrationBuilder)
            migrationBuilder.AddColumn(Of String)(
                name:="InstanceSetFilterJson",
                table:="NotificationDestinations",
                type:="TEXT",
                nullable:=True)

            migrationBuilder.AddColumn(Of String)(
                name:="NodeFilterJson",
                table:="NotificationDestinations",
                type:="TEXT",
                nullable:=True)
        End Sub

        ''' <inheritdoc />
        Protected Overrides Sub Down(migrationBuilder As MigrationBuilder)
            migrationBuilder.DropColumn(
                name:="InstanceSetFilterJson",
                table:="NotificationDestinations")

            migrationBuilder.DropColumn(
                name:="NodeFilterJson",
                table:="NotificationDestinations")
        End Sub
    End Class
End Namespace
