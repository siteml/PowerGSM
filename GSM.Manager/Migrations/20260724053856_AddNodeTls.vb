Imports Microsoft.EntityFrameworkCore.Migrations
Imports Microsoft.VisualBasic

Namespace Global.Migrations
    ''' <inheritdoc />
    Partial Public Class AddNodeTls
        Inherits Migration

        ''' <inheritdoc />
        Protected Overrides Sub Up(migrationBuilder As MigrationBuilder)
            migrationBuilder.AddColumn(Of String)(
                name:="PinnedCertFingerprint",
                table:="Nodes",
                type:="TEXT",
                maxLength:=128,
                nullable:=True)

            migrationBuilder.AddColumn(Of Integer)(
                name:="TlsPort",
                table:="Nodes",
                type:="INTEGER",
                nullable:=False,
                defaultValue:=0)

            migrationBuilder.AddColumn(Of String)(
                name:="TlsState",
                table:="Nodes",
                type:="TEXT",
                maxLength:=32,
                nullable:=True)

            migrationBuilder.AddColumn(Of Boolean)(
                name:="UseTls",
                table:="Nodes",
                type:="INTEGER",
                nullable:=False,
                defaultValue:=False)
        End Sub

        ''' <inheritdoc />
        Protected Overrides Sub Down(migrationBuilder As MigrationBuilder)
            migrationBuilder.DropColumn(
                name:="PinnedCertFingerprint",
                table:="Nodes")

            migrationBuilder.DropColumn(
                name:="TlsPort",
                table:="Nodes")

            migrationBuilder.DropColumn(
                name:="TlsState",
                table:="Nodes")

            migrationBuilder.DropColumn(
                name:="UseTls",
                table:="Nodes")
        End Sub
    End Class
End Namespace
