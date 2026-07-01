Imports System
Imports Microsoft.EntityFrameworkCore.Migrations
Imports Microsoft.VisualBasic

Namespace Global.Migrations
    ''' <inheritdoc />
    Partial Public Class AddWebSessions
        Inherits Migration

        ''' <inheritdoc />
        Protected Overrides Sub Up(migrationBuilder As MigrationBuilder)
            migrationBuilder.CreateTable(
                name:="WebSessions",
                columns:=Function(table) New With {
                    .SessionKey = table.Column(Of String)(type:="TEXT", maxLength:=200, nullable:=False),
                    .EncryptedCookieHeader = table.Column(Of Byte())(type:="BLOB", nullable:=False),
                    .CapturedAtUtc = table.Column(Of Date)(type:="TEXT", nullable:=False),
                    .CapturedByPluginId = table.Column(Of String)(type:="TEXT", maxLength:=100, nullable:=True),
                    .LastUsedUtc = table.Column(Of Date)(type:="TEXT", nullable:=True)
                },
                constraints:=Sub(table)
                    table.PrimaryKey("PK_WebSessions", Function(x) x.SessionKey)
                End Sub)
        End Sub

        ''' <inheritdoc />
        Protected Overrides Sub Down(migrationBuilder As MigrationBuilder)
            migrationBuilder.DropTable(
                name:="WebSessions")
        End Sub
    End Class
End Namespace
