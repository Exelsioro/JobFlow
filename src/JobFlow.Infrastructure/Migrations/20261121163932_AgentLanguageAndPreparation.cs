using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobFlow.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AgentLanguageAndPreparation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Tasks_CandidateProfileId",
                table: "Tasks");

            migrationBuilder.DropIndex(
                name: "IX_Tasks_WorkflowDefinitionId",
                table: "Tasks");

            migrationBuilder.AddColumn<string>(
                name: "AttentionDetails",
                table: "WorkflowRun",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "Errors",
                table: "WorkflowRun",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "PreparationOnly",
                table: "WorkflowRun",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "Skipped",
                table: "WorkflowRun",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "TargetVacancyId",
                table: "WorkflowRun",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "VacancyId",
                table: "Tasks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResponseLanguage",
                table: "Agents",
                type: "TEXT",
                nullable: false,
                defaultValue: "ru");

            migrationBuilder.AddColumn<bool>(
                name: "UseTaskModelPresets",
                table: "Agents",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Operation",
                table: "AgentExecution",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Reasoning",
                table: "AgentExecution",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ResponseLanguage",
                table: "AgentExecution",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_CandidateProfileId_VacancyId",
                table: "Tasks",
                columns: new[] { "CandidateProfileId", "VacancyId" },
                unique: true,
                filter: "\"Kind\" = 'PrepareApplication'");

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_VacancyId",
                table: "Tasks",
                column: "VacancyId");

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_WorkflowDefinitionId",
                table: "Tasks",
                column: "WorkflowDefinitionId",
                unique: true,
                filter: "\"Kind\" = 'Workflow'");

            migrationBuilder.AddForeignKey(
                name: "FK_Tasks_Vacancies_VacancyId",
                table: "Tasks",
                column: "VacancyId",
                principalTable: "Vacancies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
            migrationBuilder.Sql("""
                UPDATE AgentExecution
                SET Operation = COALESCE((SELECT json_extract(a.Details, '$.operation') FROM Audit a
                    WHERE a.EntityId = AgentExecution.Id AND a.Action = 'AgentExecutionCompleted'
                    AND json_valid(a.Details) ORDER BY a.CreatedAt DESC LIMIT 1), ''),
                    Reasoning = COALESCE((SELECT json_extract(a.Details, '$.reasoning') FROM Audit a
                    WHERE a.EntityId = AgentExecution.Id AND a.Action = 'AgentExecutionCompleted'
                    AND json_valid(a.Details) ORDER BY a.CreatedAt DESC LIMIT 1),
                    CASE WHEN json_valid(ConfigurationJson) THEN json_extract(ConfigurationJson, '$.Reasoning') END, '');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Tasks_Vacancies_VacancyId",
                table: "Tasks");

            migrationBuilder.DropIndex(
                name: "IX_Tasks_CandidateProfileId_VacancyId",
                table: "Tasks");

            migrationBuilder.DropIndex(
                name: "IX_Tasks_VacancyId",
                table: "Tasks");

            migrationBuilder.DropIndex(
                name: "IX_Tasks_WorkflowDefinitionId",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "AttentionDetails",
                table: "WorkflowRun");

            migrationBuilder.DropColumn(
                name: "Errors",
                table: "WorkflowRun");

            migrationBuilder.DropColumn(
                name: "PreparationOnly",
                table: "WorkflowRun");

            migrationBuilder.DropColumn(
                name: "Skipped",
                table: "WorkflowRun");

            migrationBuilder.DropColumn(
                name: "TargetVacancyId",
                table: "WorkflowRun");

            migrationBuilder.DropColumn(
                name: "VacancyId",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "ResponseLanguage",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "UseTaskModelPresets",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "Operation",
                table: "AgentExecution");

            migrationBuilder.DropColumn(
                name: "Reasoning",
                table: "AgentExecution");

            migrationBuilder.DropColumn(
                name: "ResponseLanguage",
                table: "AgentExecution");

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_CandidateProfileId",
                table: "Tasks",
                column: "CandidateProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_WorkflowDefinitionId",
                table: "Tasks",
                column: "WorkflowDefinitionId",
                unique: true);
        }
    }
}
