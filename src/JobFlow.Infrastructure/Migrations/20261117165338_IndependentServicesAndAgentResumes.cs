using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobFlow.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class IndependentServicesAndAgentResumes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "FreshnessDays",
                table: "WorkflowDefinition",
                type: "INTEGER",
                nullable: true,
                defaultValue: 7);

            migrationBuilder.AddColumn<string>(
                name: "Instructions",
                table: "WorkflowDefinition",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "ResumeAgentId",
                table: "WorkflowDefinition",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResumeMode",
                table: "WorkflowDefinition",
                type: "TEXT",
                nullable: false,
                defaultValue: "fixed");

            migrationBuilder.AddColumn<string>(
                name: "Availability",
                table: "Vacancies",
                type: "TEXT",
                nullable: false,
                defaultValue: "Unknown");

            migrationBuilder.AddColumn<string>(
                name: "PublicationEvidence",
                table: "Vacancies",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "VerifiedAt",
                table: "Vacancies",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DaysOfWeek",
                table: "Tasks",
                type: "TEXT",
                nullable: false,
                defaultValue: "1,2,3,4,5");

            migrationBuilder.AddColumn<string>(
                name: "LocalTime",
                table: "Tasks",
                type: "TEXT",
                nullable: false,
                defaultValue: "09:00");

            migrationBuilder.AddColumn<string>(
                name: "ScheduleKind",
                table: "Tasks",
                type: "TEXT",
                nullable: false,
                defaultValue: "interval");

            migrationBuilder.AddColumn<string>(
                name: "AdaptationJson",
                table: "ResumeVersions",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "AgentExecutionId",
                table: "ResumeVersions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DossierHash",
                table: "ResumeVersions",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ExtractedText",
                table: "ResumeVersions",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Origin",
                table: "ResumeVersions",
                type: "TEXT",
                nullable: false,
                defaultValue: "Uploaded");

            migrationBuilder.AddColumn<Guid>(
                name: "SourceResumeVersionId",
                table: "ResumeVersions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "VacancyId",
                table: "ResumeVersions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Dossier",
                table: "Profiles",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Instructions",
                table: "Integration",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TaskInstructions",
                table: "AgentExecution",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "DossierProposal",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CandidateProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ResumeVersionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AgentExecutionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FactsJson = table.Column<string>(type: "TEXT", nullable: false),
                    ProfileJson = table.Column<string>(type: "TEXT", nullable: false),
                    ConfirmedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DossierProposal", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DossierProposal_AgentExecution_AgentExecutionId",
                        column: x => x.AgentExecutionId,
                        principalTable: "AgentExecution",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DossierProposal_Profiles_CandidateProfileId",
                        column: x => x.CandidateProfileId,
                        principalTable: "Profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DossierProposal_ResumeVersions_ResumeVersionId",
                        column: x => x.ResumeVersionId,
                        principalTable: "ResumeVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Notification",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CandidateProfileId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: false),
                    Url = table.Column<string>(type: "TEXT", nullable: false),
                    RelatedId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ReadAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notification", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Notification_Profiles_CandidateProfileId",
                        column: x => x.CandidateProfileId,
                        principalTable: "Profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ServiceState",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Role = table.Column<string>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    HeartbeatAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    ProcessId = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceState", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowDefinition_ResumeAgentId",
                table: "WorkflowDefinition",
                column: "ResumeAgentId");

            migrationBuilder.CreateIndex(
                name: "IX_ResumeVersions_AgentExecutionId",
                table: "ResumeVersions",
                column: "AgentExecutionId");

            migrationBuilder.CreateIndex(
                name: "IX_ResumeVersions_SourceResumeVersionId",
                table: "ResumeVersions",
                column: "SourceResumeVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_ResumeVersions_VacancyId",
                table: "ResumeVersions",
                column: "VacancyId");

            migrationBuilder.CreateIndex(
                name: "IX_DossierProposal_AgentExecutionId",
                table: "DossierProposal",
                column: "AgentExecutionId");

            migrationBuilder.CreateIndex(
                name: "IX_DossierProposal_CandidateProfileId",
                table: "DossierProposal",
                column: "CandidateProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_DossierProposal_ResumeVersionId",
                table: "DossierProposal",
                column: "ResumeVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_Notification_CandidateProfileId",
                table: "Notification",
                column: "CandidateProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceState_Role",
                table: "ServiceState",
                column: "Role",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_ResumeVersions_AgentExecution_AgentExecutionId",
                table: "ResumeVersions",
                column: "AgentExecutionId",
                principalTable: "AgentExecution",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ResumeVersions_ResumeVersions_SourceResumeVersionId",
                table: "ResumeVersions",
                column: "SourceResumeVersionId",
                principalTable: "ResumeVersions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ResumeVersions_Vacancies_VacancyId",
                table: "ResumeVersions",
                column: "VacancyId",
                principalTable: "Vacancies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_WorkflowDefinition_Agents_ResumeAgentId",
                table: "WorkflowDefinition",
                column: "ResumeAgentId",
                principalTable: "Agents",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ResumeVersions_AgentExecution_AgentExecutionId",
                table: "ResumeVersions");

            migrationBuilder.DropForeignKey(
                name: "FK_ResumeVersions_ResumeVersions_SourceResumeVersionId",
                table: "ResumeVersions");

            migrationBuilder.DropForeignKey(
                name: "FK_ResumeVersions_Vacancies_VacancyId",
                table: "ResumeVersions");

            migrationBuilder.DropForeignKey(
                name: "FK_WorkflowDefinition_Agents_ResumeAgentId",
                table: "WorkflowDefinition");

            migrationBuilder.DropTable(
                name: "DossierProposal");

            migrationBuilder.DropTable(
                name: "Notification");

            migrationBuilder.DropTable(
                name: "ServiceState");

            migrationBuilder.DropIndex(
                name: "IX_WorkflowDefinition_ResumeAgentId",
                table: "WorkflowDefinition");

            migrationBuilder.DropIndex(
                name: "IX_ResumeVersions_AgentExecutionId",
                table: "ResumeVersions");

            migrationBuilder.DropIndex(
                name: "IX_ResumeVersions_SourceResumeVersionId",
                table: "ResumeVersions");

            migrationBuilder.DropIndex(
                name: "IX_ResumeVersions_VacancyId",
                table: "ResumeVersions");

            migrationBuilder.DropColumn(
                name: "FreshnessDays",
                table: "WorkflowDefinition");

            migrationBuilder.DropColumn(
                name: "Instructions",
                table: "WorkflowDefinition");

            migrationBuilder.DropColumn(
                name: "ResumeAgentId",
                table: "WorkflowDefinition");

            migrationBuilder.DropColumn(
                name: "ResumeMode",
                table: "WorkflowDefinition");

            migrationBuilder.DropColumn(
                name: "Availability",
                table: "Vacancies");

            migrationBuilder.DropColumn(
                name: "PublicationEvidence",
                table: "Vacancies");

            migrationBuilder.DropColumn(
                name: "VerifiedAt",
                table: "Vacancies");

            migrationBuilder.DropColumn(
                name: "DaysOfWeek",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "LocalTime",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "ScheduleKind",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "AdaptationJson",
                table: "ResumeVersions");

            migrationBuilder.DropColumn(
                name: "AgentExecutionId",
                table: "ResumeVersions");

            migrationBuilder.DropColumn(
                name: "DossierHash",
                table: "ResumeVersions");

            migrationBuilder.DropColumn(
                name: "ExtractedText",
                table: "ResumeVersions");

            migrationBuilder.DropColumn(
                name: "Origin",
                table: "ResumeVersions");

            migrationBuilder.DropColumn(
                name: "SourceResumeVersionId",
                table: "ResumeVersions");

            migrationBuilder.DropColumn(
                name: "VacancyId",
                table: "ResumeVersions");

            migrationBuilder.DropColumn(
                name: "Dossier",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "Instructions",
                table: "Integration");

            migrationBuilder.DropColumn(
                name: "TaskInstructions",
                table: "AgentExecution");
        }
    }
}
