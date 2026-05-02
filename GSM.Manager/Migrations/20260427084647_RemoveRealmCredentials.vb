Imports Microsoft.EntityFrameworkCore.Migrations
Imports Microsoft.VisualBasic

Namespace Global.Migrations
    ''' <inheritdoc />
    Partial Public Class RemoveRealmCredentials
        Inherits Migration

        ''' <inheritdoc />
        Protected Overrides Sub Up(migrationBuilder As MigrationBuilder)
            migrationBuilder.DropTable(
                name:="RealmCredentials")
        End Sub

        ''' <inheritdoc />
        Protected Overrides Sub Down(migrationBuilder As MigrationBuilder)
            migrationBuilder.CreateTable(
                name:="RealmCredentials",
                columns:=Function(table) New With {
                    .CredentialId = table.Column(Of String)(type:="TEXT", nullable:=False),
                    .DisplayName = table.Column(Of String)(type:="TEXT", nullable:=True),
                    .EncryptedCustomerKey = table.Column(Of Byte())(type:="BLOB", nullable:=True),
                    .EncryptedProviderKey = table.Column(Of Byte())(type:="BLOB", nullable:=True),
                    .GameId = table.Column(Of String)(type:="TEXT", maxLength:=100, nullable:=False)
                },
                constraints:=Sub(table)
                    table.PrimaryKey("PK_RealmCredentials", Function(x) x.CredentialId)
                End Sub)
        End Sub
    End Class
End Namespace
