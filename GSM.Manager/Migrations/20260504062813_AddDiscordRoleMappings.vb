Imports System
Imports Microsoft.EntityFrameworkCore.Migrations
Imports Microsoft.VisualBasic

Namespace Global.Migrations
    ''' <inheritdoc />
    Partial Public Class AddDiscordRoleMappings
        Inherits Migration

        ''' <inheritdoc />
        Protected Overrides Sub Up(migrationBuilder As MigrationBuilder)
            migrationBuilder.CreateTable(
                name:="DiscordRoleMappings",
                columns:=Function(table) New With {
                    .GuildId = table.Column(Of String)(type:="TEXT", maxLength:=50, nullable:=False),
                    .RoleId = table.Column(Of String)(type:="TEXT", maxLength:=50, nullable:=False),
                    .RoleName = table.Column(Of String)(type:="TEXT", maxLength:=100, nullable:=False),
                    .Permission = table.Column(Of Integer)(type:="INTEGER", nullable:=False),
                    .CreatedUtc = table.Column(Of Date)(type:="TEXT", nullable:=False),
                    .UpdatedUtc = table.Column(Of Date)(type:="TEXT", nullable:=False)
                },
                constraints:=Sub(table)
                    table.PrimaryKey("PK_DiscordRoleMappings", Function(x) New With {x.GuildId, x.RoleId})
                End Sub)
        End Sub

        ''' <inheritdoc />
        Protected Overrides Sub Down(migrationBuilder As MigrationBuilder)
            migrationBuilder.DropTable(
                name:="DiscordRoleMappings")
        End Sub
    End Class
End Namespace
