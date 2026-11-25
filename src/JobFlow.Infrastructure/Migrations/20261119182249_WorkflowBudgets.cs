using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobFlow.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class WorkflowBudgets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Checked",
                table: "WorkflowRun",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Discovered",
                table: "WorkflowRun",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MaxVacanciesPerPlatform",
                table: "WorkflowDefinition",
                type: "INTEGER",
                nullable: false,
                defaultValue: 50);

            migrationBuilder.AddColumn<int>(
                name: "MaxVacancyChecksPerRun",
                table: "WorkflowDefinition",
                type: "INTEGER",
                nullable: false,
                defaultValue: 10);

            // Reconstruct counters where durable per-run audit events are available.
            migrationBuilder.Sql("""
                UPDATE WorkflowRun SET Discovered = COALESCE((
                    SELECT SUM(CAST(json_extract(CASE WHEN json_valid(a.Details) THEN a.Details ELSE '{}' END, '$.details.count') AS INTEGER))
                    FROM Audit a WHERE a.EntityId = WorkflowRun.Id
                    AND a.Action = 'WorkflowDiscoveryCompleted' AND json_valid(a.Details)), 0);
                UPDATE WorkflowRun SET Checked = (
                    SELECT COUNT(DISTINCT json_extract(CASE WHEN json_valid(a.Details) THEN a.Details ELSE '{}' END, '$.details.vacancyId'))
                    FROM Audit a WHERE a.EntityId = WorkflowRun.Id
                    AND a.Action IN ('WorkflowVacancyEvaluated', 'WorkflowApplicationBlocked') AND json_valid(a.Details));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Checked",
                table: "WorkflowRun");

            migrationBuilder.DropColumn(
                name: "Discovered",
                table: "WorkflowRun");

            migrationBuilder.DropColumn(
                name: "MaxVacanciesPerPlatform",
                table: "WorkflowDefinition");

            migrationBuilder.DropColumn(
                name: "MaxVacancyChecksPerRun",
                table: "WorkflowDefinition");
        }
    }
}
