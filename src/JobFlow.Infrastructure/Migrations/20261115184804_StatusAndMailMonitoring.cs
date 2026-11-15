using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobFlow.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class StatusAndMailMonitoring : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "MailAgentId",
                table: "Integration",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Analyzed",
                table: "FeedbackEvent",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Category",
                table: "FeedbackEvent",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Category",
                table: "EmployerInteraction",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ResponseStatus",
                table: "Applications",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "ResponseStatusAt",
                table: "Applications",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MailAgentId",
                table: "Integration");

            migrationBuilder.DropColumn(
                name: "Analyzed",
                table: "FeedbackEvent");

            migrationBuilder.DropColumn(
                name: "Category",
                table: "FeedbackEvent");

            migrationBuilder.DropColumn(
                name: "Category",
                table: "EmployerInteraction");

            migrationBuilder.DropColumn(
                name: "ResponseStatus",
                table: "Applications");

            migrationBuilder.DropColumn(
                name: "ResponseStatusAt",
                table: "Applications");
        }
    }
}
