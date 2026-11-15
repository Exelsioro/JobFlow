using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobFlow.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class JournalImportAndFeedback : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ConflictPolicy",
                table: "Integration",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "Enabled",
                table: "Integration",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Endpoint",
                table: "Integration",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "JobPlatformId",
                table: "Integration",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastCheckedAt",
                table: "Integration",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastStatus",
                table: "Integration",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "MappingJson",
                table: "Integration",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "FeedbackEvent",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    IntegrationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CandidateProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceEventId = table.Column<string>(type: "TEXT", nullable: false),
                    Subject = table.Column<string>(type: "TEXT", nullable: false),
                    Body = table.Column<string>(type: "TEXT", nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    NeedsAction = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeedbackEvent", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FeedbackEvent_Integration_IntegrationId",
                        column: x => x.IntegrationId,
                        principalTable: "Integration",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FeedbackEvent_Profiles_CandidateProfileId",
                        column: x => x.CandidateProfileId,
                        principalTable: "Profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "HistoricalApplication",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    IntegrationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CandidateProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    VacancyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceState = table.Column<string>(type: "TEXT", nullable: false),
                    SourceKey = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HistoricalApplication", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HistoricalApplication_Integration_IntegrationId",
                        column: x => x.IntegrationId,
                        principalTable: "Integration",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_HistoricalApplication_Profiles_CandidateProfileId",
                        column: x => x.CandidateProfileId,
                        principalTable: "Profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_HistoricalApplication_Vacancies_VacancyId",
                        column: x => x.VacancyId,
                        principalTable: "Vacancies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "JournalImport",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    IntegrationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    ContentHash = table.Column<string>(type: "TEXT", nullable: false),
                    RowsJson = table.Column<string>(type: "TEXT", nullable: false),
                    RowCount = table.Column<int>(type: "INTEGER", nullable: false),
                    Added = table.Column<int>(type: "INTEGER", nullable: false),
                    Duplicates = table.Column<int>(type: "INTEGER", nullable: false),
                    Conflicts = table.Column<int>(type: "INTEGER", nullable: false),
                    CommittedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JournalImport", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JournalImport_Integration_IntegrationId",
                        column: x => x.IntegrationId,
                        principalTable: "Integration",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Integration_JobPlatformId",
                table: "Integration",
                column: "JobPlatformId");

            migrationBuilder.CreateIndex(
                name: "IX_FeedbackEvent_CandidateProfileId",
                table: "FeedbackEvent",
                column: "CandidateProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_FeedbackEvent_IntegrationId_SourceEventId",
                table: "FeedbackEvent",
                columns: new[] { "IntegrationId", "SourceEventId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HistoricalApplication_CandidateProfileId_VacancyId",
                table: "HistoricalApplication",
                columns: new[] { "CandidateProfileId", "VacancyId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HistoricalApplication_IntegrationId",
                table: "HistoricalApplication",
                column: "IntegrationId");

            migrationBuilder.CreateIndex(
                name: "IX_HistoricalApplication_VacancyId",
                table: "HistoricalApplication",
                column: "VacancyId");

            migrationBuilder.CreateIndex(
                name: "IX_JournalImport_IntegrationId",
                table: "JournalImport",
                column: "IntegrationId");

            migrationBuilder.AddForeignKey(
                name: "FK_Integration_Platforms_JobPlatformId",
                table: "Integration",
                column: "JobPlatformId",
                principalTable: "Platforms",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Integration_Platforms_JobPlatformId",
                table: "Integration");

            migrationBuilder.DropTable(
                name: "FeedbackEvent");

            migrationBuilder.DropTable(
                name: "HistoricalApplication");

            migrationBuilder.DropTable(
                name: "JournalImport");

            migrationBuilder.DropIndex(
                name: "IX_Integration_JobPlatformId",
                table: "Integration");

            migrationBuilder.DropColumn(
                name: "ConflictPolicy",
                table: "Integration");

            migrationBuilder.DropColumn(
                name: "Enabled",
                table: "Integration");

            migrationBuilder.DropColumn(
                name: "Endpoint",
                table: "Integration");

            migrationBuilder.DropColumn(
                name: "JobPlatformId",
                table: "Integration");

            migrationBuilder.DropColumn(
                name: "LastCheckedAt",
                table: "Integration");

            migrationBuilder.DropColumn(
                name: "LastStatus",
                table: "Integration");

            migrationBuilder.DropColumn(
                name: "MappingJson",
                table: "Integration");
        }
    }
}
