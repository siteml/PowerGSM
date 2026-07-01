Imports System
Imports Microsoft.EntityFrameworkCore.Migrations
Imports Microsoft.VisualBasic

Namespace Global.Migrations
    ''' <inheritdoc />
    Partial Public Class PluginSources
        Inherits Migration

        ''' <inheritdoc />
        Protected Overrides Sub Up(migrationBuilder As MigrationBuilder)
            migrationBuilder.CreateTable(
                name:="PluginSources",
                columns:=Function(table) New With {
                    .SourceId = table.Column(Of String)(type:="TEXT", nullable:=False),
                    .DisplayName = table.Column(Of String)(type:="TEXT", maxLength:=200, nullable:=False),
                    .Owner = table.Column(Of String)(type:="TEXT", maxLength:=100, nullable:=False),
                    .Repo = table.Column(Of String)(type:="TEXT", maxLength:=100, nullable:=False),
                    .RepoPath = table.Column(Of String)(type:="TEXT", maxLength:=300, nullable:=True),
                    .Branch = table.Column(Of String)(type:="TEXT", maxLength:=100, nullable:=True),
                    .IsOfficial = table.Column(Of Boolean)(type:="INTEGER", nullable:=False),
                    .IsEnabled = table.Column(Of Boolean)(type:="INTEGER", nullable:=False),
                    .LastFetchedUtc = table.Column(Of Date)(type:="TEXT", nullable:=True)
                },
                constraints:=Sub(table)
                    table.PrimaryKey("PK_PluginSources", Function(x) x.SourceId)
                End Sub)

            migrationBuilder.CreateIndex(
                name:="IX_PluginSources_Owner_Repo_RepoPath",
                table:="PluginSources",
                columns:={"Owner", "Repo", "RepoPath"},
                unique:=True)
        End Sub

        ''' <inheritdoc />
        Protected Overrides Sub Down(migrationBuilder As MigrationBuilder)
            migrationBuilder.DropTable(
                name:="PluginSources")
        End Sub
    End Class
End Namespace
