Imports Microsoft.EntityFrameworkCore.Migrations
Imports Microsoft.VisualBasic

Namespace Global.Migrations
    ''' <inheritdoc />
    Partial Public Class Phase5g1_ChatIdentity
        Inherits Migration

        ''' <inheritdoc />
        Protected Overrides Sub Up(migrationBuilder As MigrationBuilder)
            migrationBuilder.RenameColumn(
                name:="PlayerName",
                table:="ChatMessages",
                newName:="DisplayName")

            migrationBuilder.AddColumn(Of String)(
                name:="CharacterId",
                table:="ChatMessages",
                type:="TEXT",
                maxLength:=64,
                nullable:=True)

            migrationBuilder.AddColumn(Of String)(
                name:="PlatformUserId",
                table:="ChatMessages",
                type:="TEXT",
                maxLength:=64,
                nullable:=True)

            migrationBuilder.CreateIndex(
                name:="IX_ChatMessages_CharacterId",
                table:="ChatMessages",
                column:="CharacterId")
        End Sub

        ''' <inheritdoc />
        Protected Overrides Sub Down(migrationBuilder As MigrationBuilder)
            migrationBuilder.DropIndex(
                name:="IX_ChatMessages_CharacterId",
                table:="ChatMessages")

            migrationBuilder.DropColumn(
                name:="CharacterId",
                table:="ChatMessages")

            migrationBuilder.DropColumn(
                name:="PlatformUserId",
                table:="ChatMessages")

            migrationBuilder.RenameColumn(
                name:="DisplayName",
                table:="ChatMessages",
                newName:="PlayerName")
        End Sub
    End Class
End Namespace
