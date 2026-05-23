Imports System
Imports Microsoft.EntityFrameworkCore.Migrations
Imports Microsoft.VisualBasic

Namespace Global.Migrations
    ''' <inheritdoc />
    Partial Public Class Phase5h_SharedConfigGroups
        Inherits Migration

        ''' <inheritdoc />
        Protected Overrides Sub Up(migrationBuilder As MigrationBuilder)
            migrationBuilder.AddColumn(Of String)(
                name:="SharedConfigGroupId",
                table:="Installations",
                type:="TEXT",
                nullable:=True)

            migrationBuilder.CreateTable(
                name:="SharedConfigGroups",
                columns:=Function(table) New With {
                    .GroupId = table.Column(Of String)(type:="TEXT", nullable:=False),
                    .PluginId = table.Column(Of String)(type:="TEXT", maxLength:=100, nullable:=False),
                    .GroupType = table.Column(Of String)(type:="TEXT", maxLength:=50, nullable:=False),
                    .DisplayName = table.Column(Of String)(type:="TEXT", maxLength:=200, nullable:=False),
                    .ConfigJson = table.Column(Of String)(type:="TEXT", nullable:=False),
                    .CreatedUtc = table.Column(Of Date)(type:="TEXT", nullable:=False),
                    .UpdatedUtc = table.Column(Of Date)(type:="TEXT", nullable:=False)
                },
                constraints:=Sub(table)
                    table.PrimaryKey("PK_SharedConfigGroups", Function(x) x.GroupId)
                End Sub)

            migrationBuilder.CreateIndex(
                name:="IX_Installations_SharedConfigGroupId",
                table:="Installations",
                column:="SharedConfigGroupId")

            migrationBuilder.CreateIndex(
                name:="IX_SharedConfigGroups_PluginId_GroupType",
                table:="SharedConfigGroups",
                columns:={"PluginId", "GroupType"})

            migrationBuilder.AddForeignKey(
                name:="FK_Installations_SharedConfigGroups_SharedConfigGroupId",
                table:="Installations",
                column:="SharedConfigGroupId",
                principalTable:="SharedConfigGroups",
                principalColumn:="GroupId")
        End Sub

        ''' <inheritdoc />
        Protected Overrides Sub Down(migrationBuilder As MigrationBuilder)
            migrationBuilder.DropForeignKey(
                name:="FK_Installations_SharedConfigGroups_SharedConfigGroupId",
                table:="Installations")

            migrationBuilder.DropTable(
                name:="SharedConfigGroups")

            migrationBuilder.DropIndex(
                name:="IX_Installations_SharedConfigGroupId",
                table:="Installations")

            migrationBuilder.DropColumn(
                name:="SharedConfigGroupId",
                table:="Installations")
        End Sub
    End Class
End Namespace
