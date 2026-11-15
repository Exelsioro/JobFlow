using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobFlow.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SearchWorkflow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Currency",
                table: "Vacancies",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Level",
                table: "Vacancies",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Location",
                table: "Vacancies",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "PublishedAt",
                table: "Vacancies",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequiredSkills",
                table: "Vacancies",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<double>(
                name: "SalaryMax",
                table: "Vacancies",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WorkMode",
                table: "Vacancies",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "WorkflowDefinitionId",
                table: "Tasks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "WorkflowDefinitionId",
                table: "Applications",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "WorkflowDefinition",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    CandidateProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    JobPlatformId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ResumeVersionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EvaluationAgentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    LetterAgentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    RulesJson = table.Column<string>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    LeaseUntil = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LeaseToken = table.Column<string>(type: "TEXT", nullable: true),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowDefinition", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkflowDefinition_Agents_EvaluationAgentId",
                        column: x => x.EvaluationAgentId,
                        principalTable: "Agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkflowDefinition_Agents_LetterAgentId",
                        column: x => x.LetterAgentId,
                        principalTable: "Agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkflowDefinition_Platforms_JobPlatformId",
                        column: x => x.JobPlatformId,
                        principalTable: "Platforms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkflowDefinition_Profiles_CandidateProfileId",
                        column: x => x.CandidateProfileId,
                        principalTable: "Profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkflowDefinition_ResumeVersions_ResumeVersionId",
                        column: x => x.ResumeVersionId,
                        principalTable: "ResumeVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowRun",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkflowDefinitionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CandidateProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Stage = table.Column<string>(type: "TEXT", nullable: false),
                    Prepared = table.Column<int>(type: "INTEGER", nullable: false),
                    Rejected = table.Column<int>(type: "INTEGER", nullable: false),
                    Error = table.Column<string>(type: "TEXT", nullable: false),
                    FinishedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowRun", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkflowRun_WorkflowDefinition_WorkflowDefinitionId",
                        column: x => x.WorkflowDefinitionId,
                        principalTable: "WorkflowDefinition",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_WorkflowDefinitionId",
                table: "Tasks",
                column: "WorkflowDefinitionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Applications_WorkflowDefinitionId",
                table: "Applications",
                column: "WorkflowDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowDefinition_CandidateProfileId",
                table: "WorkflowDefinition",
                column: "CandidateProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowDefinition_EvaluationAgentId",
                table: "WorkflowDefinition",
                column: "EvaluationAgentId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowDefinition_JobPlatformId",
                table: "WorkflowDefinition",
                column: "JobPlatformId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowDefinition_LetterAgentId",
                table: "WorkflowDefinition",
                column: "LetterAgentId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowDefinition_ResumeVersionId",
                table: "WorkflowDefinition",
                column: "ResumeVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowRun_WorkflowDefinitionId",
                table: "WorkflowRun",
                column: "WorkflowDefinitionId");

            migrationBuilder.AddForeignKey(
                name: "FK_Applications_WorkflowDefinition_WorkflowDefinitionId",
                table: "Applications",
                column: "WorkflowDefinitionId",
                principalTable: "WorkflowDefinition",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Tasks_WorkflowDefinition_WorkflowDefinitionId",
                table: "Tasks",
                column: "WorkflowDefinitionId",
                principalTable: "WorkflowDefinition",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Applications_WorkflowDefinition_WorkflowDefinitionId",
                table: "Applications");

            migrationBuilder.DropForeignKey(
                name: "FK_Tasks_WorkflowDefinition_WorkflowDefinitionId",
                table: "Tasks");

            migrationBuilder.DropTable(
                name: "WorkflowRun");

            migrationBuilder.DropTable(
                name: "WorkflowDefinition");

            migrationBuilder.DropIndex(
                name: "IX_Tasks_WorkflowDefinitionId",
                table: "Tasks");

            migrationBuilder.DropIndex(
                name: "IX_Applications_WorkflowDefinitionId",
                table: "Applications");

            migrationBuilder.DropColumn(
                name: "Currency",
                table: "Vacancies");

            migrationBuilder.DropColumn(
                name: "Level",
                table: "Vacancies");

            migrationBuilder.DropColumn(
                name: "Location",
                table: "Vacancies");

            migrationBuilder.DropColumn(
                name: "PublishedAt",
                table: "Vacancies");

            migrationBuilder.DropColumn(
                name: "RequiredSkills",
                table: "Vacancies");

            migrationBuilder.DropColumn(
                name: "SalaryMax",
                table: "Vacancies");

            migrationBuilder.DropColumn(
                name: "WorkMode",
                table: "Vacancies");

            migrationBuilder.DropColumn(
                name: "WorkflowDefinitionId",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "WorkflowDefinitionId",
                table: "Applications");
        }
    }
}
