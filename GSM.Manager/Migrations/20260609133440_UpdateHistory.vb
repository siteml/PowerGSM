Imports System
Imports Microsoft.EntityFrameworkCore.Migrations
Imports Microsoft.VisualBasic

Namespace Global.Migrations
    ''' <inheritdoc />
    Partial Public Class UpdateHistory
        Inherits Migration

        ''' <inheritdoc />
        Protected Overrides Sub Up(migrationBuilder As MigrationBuilder)
            migrationBuilder.CreateTable(
                name:="UpdateHistory",
                columns:=Function(table) New With {
                    .HistoryId = table.Column(Of String)(type:="TEXT", nullable:=False),
                    .AppliedAtUtc = table.Column(Of Date)(type:="TEXT", nullable:=False),
                    .FromVersion = table.Column(Of String)(type:="TEXT", maxLength:=100, nullable:=True),
                    .ToVersion = table.Column(Of String)(type:="TEXT", maxLength:=100, nullable:=True),
                    .Outcome = table.Column(Of String)(type:="TEXT", maxLength:=40, nullable:=False),
                    .Detail = table.Column(Of String)(type:="TEXT", nullable:=True)
                },
                constraints:=Sub(table)
                    table.PrimaryKey("PK_UpdateHistory", Function(x) x.HistoryId)
                End Sub)

            migrationBuilder.CreateIndex(
                name:="IX_UpdateHistory_AppliedAtUtc",
                table:="UpdateHistory",
                column:="AppliedAtUtc")
        End Sub

        ''' <inheritdoc />
        Protected Overrides Sub Down(migrationBuilder As MigrationBuilder)
            migrationBuilder.DropTable(
                name:="UpdateHistory")
        End Sub
    End Class
End Namespace
