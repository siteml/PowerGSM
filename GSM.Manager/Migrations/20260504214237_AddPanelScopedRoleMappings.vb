Imports Microsoft.EntityFrameworkCore.Migrations
Imports Microsoft.VisualBasic

Namespace Global.Migrations
    ''' <inheritdoc />
    Partial Public Class AddPanelScopedRoleMappings
        Inherits Migration

        ''' <inheritdoc />
        Protected Overrides Sub Up(migrationBuilder As MigrationBuilder)
            migrationBuilder.DropPrimaryKey(
                name:="PK_DiscordRoleMappings",
                table:="DiscordRoleMappings")

            migrationBuilder.AddColumn(Of String)(
                name:="PanelId",
                table:="DiscordRoleMappings",
                type:="TEXT",
                maxLength:=64,
                nullable:=False,
                defaultValue:="")

            migrationBuilder.AddPrimaryKey(
                name:="PK_DiscordRoleMappings",
                table:="DiscordRoleMappings",
                columns:={"GuildId", "PanelId", "RoleId"})
        End Sub

        ''' <inheritdoc />
        Protected Overrides Sub Down(migrationBuilder As MigrationBuilder)
            migrationBuilder.DropPrimaryKey(
                name:="PK_DiscordRoleMappings",
                table:="DiscordRoleMappings")

            migrationBuilder.DropColumn(
                name:="PanelId",
                table:="DiscordRoleMappings")

            migrationBuilder.AddPrimaryKey(
                name:="PK_DiscordRoleMappings",
                table:="DiscordRoleMappings",
                columns:={"GuildId", "RoleId"})
        End Sub
    End Class
End Namespace
