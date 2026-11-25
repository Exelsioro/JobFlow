using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace JobFlow.Infrastructure.Migrations;

[DbContext(typeof(JobFlowDb))]
[Migration("20261123010000_PreparationAgeException")]
public sealed class PreparationAgeException : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddColumn<bool>("PrepareOutsideAgeLimit", "Tasks", nullable: false, defaultValue: false);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropColumn("PrepareOutsideAgeLimit", "Tasks");
}
