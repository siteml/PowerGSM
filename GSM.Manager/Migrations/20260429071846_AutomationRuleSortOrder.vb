Imports Microsoft.EntityFrameworkCore.Migrations
Imports Microsoft.VisualBasic

Namespace Global.Migrations
    ''' <inheritdoc />
    Partial Public Class AutomationRuleSortOrder
        Inherits Migration

        ''' <inheritdoc />
        Protected Overrides Sub Up(migrationBuilder As MigrationBuilder)
            migrationBuilder.AddColumn(Of Integer)(
                name:="SortOrder",
                table:="AutomationRules",
                type:="INTEGER",
                nullable:=False,
                defaultValue:=0)

            migrationBuilder.CreateIndex(
                name:="IX_AutomationRules_SortOrder",
                table:="AutomationRules",
                column:="SortOrder")
        End Sub

        ''' <inheritdoc />
        Protected Overrides Sub Down(migrationBuilder As MigrationBuilder)
            migrationBuilder.DropIndex(
                name:="IX_AutomationRules_SortOrder",
                table:="AutomationRules")

            migrationBuilder.DropColumn(
                name:="SortOrder",
                table:="AutomationRules")
        End Sub
    End Class
End Namespace
