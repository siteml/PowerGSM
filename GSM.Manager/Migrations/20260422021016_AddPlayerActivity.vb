Imports System
Imports Microsoft.EntityFrameworkCore.Migrations
Imports Microsoft.VisualBasic

Namespace Global.Migrations
    ''' <inheritdoc />
    Partial Public Class AddPlayerActivity
        Inherits Migration

        ''' <inheritdoc />
        Protected Overrides Sub Up(migrationBuilder As MigrationBuilder)
            migrationBuilder.CreateTable(
                name:="PlayerActivity",
                columns:=Function(table) New With {
                    .ActivityId = table.Column(Of String)(type:="TEXT", nullable:=False),
                    .SessionIdentity = table.Column(Of String)(type:="TEXT", maxLength:=200, nullable:=False),
                    .NodeId = table.Column(Of String)(type:="TEXT", maxLength:=100, nullable:=True),
                    .InstanceId = table.Column(Of String)(type:="TEXT", maxLength:=100, nullable:=True),
                    .TimestampUtc = table.Column(Of Date)(type:="TEXT", nullable:=False),
                    .PlayerName = table.Column(Of String)(type:="TEXT", maxLength:=100, nullable:=False),
                    .EventKind = table.Column(Of String)(type:="TEXT", maxLength:=20, nullable:=False)
                },
                constraints:=Sub(table)
                    table.PrimaryKey("PK_PlayerActivity", Function(x) x.ActivityId)
                End Sub)

            migrationBuilder.CreateIndex(
                name:="IX_PlayerActivity_PlayerName_TimestampUtc",
                table:="PlayerActivity",
                columns:={"PlayerName", "TimestampUtc"})

            migrationBuilder.CreateIndex(
                name:="IX_PlayerActivity_SessionIdentity_TimestampUtc",
                table:="PlayerActivity",
                columns:={"SessionIdentity", "TimestampUtc"})
        End Sub

        ''' <inheritdoc />
        Protected Overrides Sub Down(migrationBuilder As MigrationBuilder)
            migrationBuilder.DropTable(
                name:="PlayerActivity")
        End Sub
    End Class
End Namespace
