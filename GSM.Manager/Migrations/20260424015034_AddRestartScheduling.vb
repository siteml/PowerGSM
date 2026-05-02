Imports Microsoft.EntityFrameworkCore.Migrations
Imports Microsoft.VisualBasic

Namespace Global.Migrations
    ''' <inheritdoc />
    Partial Public Class AddRestartScheduling
        Inherits Migration

        ''' <inheritdoc />
        Protected Overrides Sub Up(migrationBuilder As MigrationBuilder)
            migrationBuilder.AddColumn(Of Integer)(
                name:="MaxConcurrentRestarts",
                table:="Nodes",
                type:="INTEGER",
                nullable:=False,
                defaultValue:=0)

            migrationBuilder.AddColumn(Of String)(
                name:="RestartCron",
                table:="Instances",
                type:="TEXT",
                maxLength:=100,
                nullable:=True)

            migrationBuilder.AddColumn(Of Boolean)(
                name:="RestartEnabled",
                table:="Instances",
                type:="INTEGER",
                nullable:=False,
                defaultValue:=False)

            migrationBuilder.AddColumn(Of String)(
                name:="RestartRuleId",
                table:="Instances",
                type:="TEXT",
                maxLength:=100,
                nullable:=True)

            migrationBuilder.AddColumn(Of Integer)(
                name:="MaxConcurrentRestarts",
                table:="Installations",
                type:="INTEGER",
                nullable:=False,
                defaultValue:=0)
        End Sub

        ''' <inheritdoc />
        Protected Overrides Sub Down(migrationBuilder As MigrationBuilder)
            migrationBuilder.DropColumn(
                name:="MaxConcurrentRestarts",
                table:="Nodes")

            migrationBuilder.DropColumn(
                name:="RestartCron",
                table:="Instances")

            migrationBuilder.DropColumn(
                name:="RestartEnabled",
                table:="Instances")

            migrationBuilder.DropColumn(
                name:="RestartRuleId",
                table:="Instances")

            migrationBuilder.DropColumn(
                name:="MaxConcurrentRestarts",
                table:="Installations")
        End Sub
    End Class
End Namespace
