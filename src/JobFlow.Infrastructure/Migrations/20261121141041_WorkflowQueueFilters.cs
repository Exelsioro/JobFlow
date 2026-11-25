using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobFlow.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class WorkflowQueueFilters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ApplicationStatusFilter",
                table: "WorkflowDefinition",
                type: "TEXT",
                nullable: false,
                defaultValue: "pending");

            migrationBuilder.AddColumn<string>(
                name: "EvaluationResultFilter",
                table: "WorkflowDefinition",
                type: "TEXT",
                nullable: false,
                defaultValue: "all");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ApplicationStatusFilter",
                table: "WorkflowDefinition");

            migrationBuilder.DropColumn(
                name: "EvaluationResultFilter",
                table: "WorkflowDefinition");
        }
    }
}
