Imports Microsoft.EntityFrameworkCore.Migrations
Imports Microsoft.VisualBasic

Namespace Global.Migrations
    ''' <inheritdoc />
    Partial Public Class Phase5g2_PlayerActivity_Identity
        Inherits Migration

        ''' <inheritdoc />
        Protected Overrides Sub Up(migrationBuilder As MigrationBuilder)
            migrationBuilder.AddColumn(Of String)(
                name:="CharacterId",
                table:="PlayerActivity",
                type:="TEXT",
                maxLength:=64,
                nullable:=True)

            migrationBuilder.AddColumn(Of String)(
                name:="DisplayName",
                table:="PlayerActivity",
                type:="TEXT",
                maxLength:=100,
                nullable:=True)

            migrationBuilder.AddColumn(Of String)(
                name:="PlatformUserId",
                table:="PlayerActivity",
                type:="TEXT",
                maxLength:=64,
                nullable:=True)

            migrationBuilder.CreateIndex(
                name:="IX_PlayerActivity_CharacterId",
                table:="PlayerActivity",
                column:="CharacterId")
        End Sub

        ''' <inheritdoc />
        Protected Overrides Sub Down(migrationBuilder As MigrationBuilder)
            migrationBuilder.DropIndex(
                name:="IX_PlayerActivity_CharacterId",
                table:="PlayerActivity")

            migrationBuilder.DropColumn(
                name:="CharacterId",
                table:="PlayerActivity")

            migrationBuilder.DropColumn(
                name:="DisplayName",
                table:="PlayerActivity")

            migrationBuilder.DropColumn(
                name:="PlatformUserId",
                table:="PlayerActivity")
        End Sub
    End Class
End Namespace
