using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobFlow.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BrowserAgentWorkflow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "NeedsAttention",
                table: "WorkflowRun",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Submitted",
                table: "WorkflowRun",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "AutoSubmit",
                table: "WorkflowDefinition",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "BrowserAgentId",
                table: "WorkflowDefinition",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "BrowserProfileId",
                table: "WorkflowDefinition",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "CheckMail",
                table: "WorkflowDefinition",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ExecutionMode",
                table: "WorkflowDefinition",
                type: "TEXT",
                nullable: false,
                defaultValue: "prepare");

            migrationBuilder.AddColumn<int>(
                name: "MaxApplicationsPerRun",
                table: "WorkflowDefinition",
                type: "INTEGER",
                nullable: false,
                defaultValue: 10);

            migrationBuilder.AddColumn<int>(
                name: "MaxBrowserSteps",
                table: "WorkflowDefinition",
                type: "INTEGER",
                nullable: false,
                defaultValue: 40);

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowDefinition_BrowserAgentId",
                table: "WorkflowDefinition",
                column: "BrowserAgentId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowDefinition_BrowserProfileId",
                table: "WorkflowDefinition",
                column: "BrowserProfileId");

            migrationBuilder.AddForeignKey(
                name: "FK_WorkflowDefinition_Agents_BrowserAgentId",
                table: "WorkflowDefinition",
                column: "BrowserAgentId",
                principalTable: "Agents",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_WorkflowDefinition_BrowserProfile_BrowserProfileId",
                table: "WorkflowDefinition",
                column: "BrowserProfileId",
                principalTable: "BrowserProfile",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_WorkflowDefinition_Agents_BrowserAgentId",
                table: "WorkflowDefinition");

            migrationBuilder.DropForeignKey(
                name: "FK_WorkflowDefinition_BrowserProfile_BrowserProfileId",
                table: "WorkflowDefinition");

            migrationBuilder.DropIndex(
                name: "IX_WorkflowDefinition_BrowserAgentId",
                table: "WorkflowDefinition");

            migrationBuilder.DropIndex(
                name: "IX_WorkflowDefinition_BrowserProfileId",
                table: "WorkflowDefinition");

            migrationBuilder.DropColumn(
                name: "NeedsAttention",
                table: "WorkflowRun");

            migrationBuilder.DropColumn(
                name: "Submitted",
                table: "WorkflowRun");

            migrationBuilder.DropColumn(
                name: "AutoSubmit",
                table: "WorkflowDefinition");

            migrationBuilder.DropColumn(
                name: "BrowserAgentId",
                table: "WorkflowDefinition");

            migrationBuilder.DropColumn(
                name: "BrowserProfileId",
                table: "WorkflowDefinition");

            migrationBuilder.DropColumn(
                name: "CheckMail",
                table: "WorkflowDefinition");

            migrationBuilder.DropColumn(
                name: "ExecutionMode",
                table: "WorkflowDefinition");

            migrationBuilder.DropColumn(
                name: "MaxApplicationsPerRun",
                table: "WorkflowDefinition");

            migrationBuilder.DropColumn(
                name: "MaxBrowserSteps",
                table: "WorkflowDefinition");
        }
    }
}
