Imports Microsoft.EntityFrameworkCore.Migrations
Imports Microsoft.VisualBasic

Namespace Global.Migrations
    ''' <inheritdoc />
    Partial Public Class AddInstanceSortOrder
        Inherits Migration

        ''' <inheritdoc />
        Protected Overrides Sub Up(migrationBuilder As MigrationBuilder)
            migrationBuilder.DropIndex(
                name:="IX_Instances_InstallationId",
                table:="Instances")

            migrationBuilder.AddColumn(Of Integer)(
                name:="SortOrder",
                table:="Instances",
                type:="INTEGER",
                nullable:=False,
                defaultValue:=0)
            ' Backfill SortOrder for existing instances using ROW_NUMBER
            ' partitioned by InstallationId, ordered by CreatedUtc.
            ' This gives each installation's instances a stable 1..N
            ' ordering matching their creation sequence. New rows
            ' added after this migration will use NextSortOrder(max+1).
            '
            ' Implementation: SQLite doesn't let a window function
            ' correlate with the outer UPDATE target directly — an
            ' earlier attempt filtered the inner query to a single
            ' row BEFORE ROW_NUMBER ran, which yielded 1 for every
            ' row (whole-table bug caught during Phase 4a testing).
            ' The fix: pre-compute (InstanceId -> row_number) in a
            ' CTE over the full Instances table, then UPDATE by
            ' joining back on InstanceId.
            migrationBuilder.Sql(
            "WITH numbered AS (" &
            "  SELECT InstanceId, ROW_NUMBER() OVER (" &
            "    PARTITION BY InstallationId ORDER BY CreatedUtc, InstanceId" &
            "  ) AS rn FROM Instances" &
            ") " &
            "UPDATE Instances SET SortOrder = (" &
            "  SELECT rn FROM numbered WHERE numbered.InstanceId = Instances.InstanceId" &
            ")")
            migrationBuilder.CreateIndex(
                name:="IX_Instances_InstallationId_SortOrder",
                table:="Instances",
                columns:={"InstallationId", "SortOrder"})
        End Sub

        ''' <inheritdoc />
        Protected Overrides Sub Down(migrationBuilder As MigrationBuilder)
            migrationBuilder.DropIndex(
                name:="IX_Instances_InstallationId_SortOrder",
                table:="Instances")

            migrationBuilder.DropColumn(
                name:="SortOrder",
                table:="Instances")

            migrationBuilder.CreateIndex(
                name:="IX_Instances_InstallationId",
                table:="Instances",
                column:="InstallationId")
        End Sub
    End Class
End Namespace
