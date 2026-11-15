using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobFlow.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class McpConnections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Enabled",
                table: "ToolConnection",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastCheckedAt",
                table: "ToolConnection",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "ToolConnection",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ToolsJson",
                table: "ToolConnection",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ToolBindingsJson",
                table: "Agents",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Enabled",
                table: "ToolConnection");

            migrationBuilder.DropColumn(
                name: "LastCheckedAt",
                table: "ToolConnection");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "ToolConnection");

            migrationBuilder.DropColumn(
                name: "ToolsJson",
                table: "ToolConnection");

            migrationBuilder.DropColumn(
                name: "ToolBindingsJson",
                table: "Agents");
        }
    }
}
