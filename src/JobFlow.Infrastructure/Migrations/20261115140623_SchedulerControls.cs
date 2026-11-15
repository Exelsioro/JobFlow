using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobFlow.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SchedulerControls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CancelRequested",
                table: "Tasks",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "FailureCount",
                table: "Tasks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "IntegrationId",
                table: "Tasks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxRetries",
                table: "Tasks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TimeoutSeconds",
                table: "Tasks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_IntegrationId",
                table: "Tasks",
                column: "IntegrationId");

            migrationBuilder.AddForeignKey(
                name: "FK_Tasks_Integration_IntegrationId",
                table: "Tasks",
                column: "IntegrationId",
                principalTable: "Integration",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Tasks_Integration_IntegrationId",
                table: "Tasks");

            migrationBuilder.DropIndex(
                name: "IX_Tasks_IntegrationId",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "CancelRequested",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "FailureCount",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "IntegrationId",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "MaxRetries",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "TimeoutSeconds",
                table: "Tasks");
        }
    }
}
