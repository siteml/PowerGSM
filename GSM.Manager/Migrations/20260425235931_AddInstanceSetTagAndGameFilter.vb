Imports Microsoft.EntityFrameworkCore.Migrations
Imports Microsoft.VisualBasic

Namespace Global.Migrations
    ''' <inheritdoc />
    Partial Public Class AddInstanceSetTagAndGameFilter
        Inherits Migration

        ''' <inheritdoc />
        Protected Overrides Sub Up(migrationBuilder As MigrationBuilder)
            migrationBuilder.AddColumn(Of String)(
                name:="InstanceSetTag",
                table:="Instances",
                type:="TEXT",
                maxLength:=100,
                nullable:=True)

            migrationBuilder.AddColumn(Of String)(
                name:="GameFilter",
                table:="AutomationRules",
                type:="TEXT",
                maxLength:=100,
                nullable:=True)

            migrationBuilder.CreateIndex(
                name:="IX_Instances_InstanceSetTag",
                table:="Instances",
                column:="InstanceSetTag")
        End Sub

        ''' <inheritdoc />
        Protected Overrides Sub Down(migrationBuilder As MigrationBuilder)
            migrationBuilder.DropIndex(
                name:="IX_Instances_InstanceSetTag",
                table:="Instances")

            migrationBuilder.DropColumn(
                name:="InstanceSetTag",
                table:="Instances")

            migrationBuilder.DropColumn(
                name:="GameFilter",
                table:="AutomationRules")
        End Sub
    End Class
End Namespace
