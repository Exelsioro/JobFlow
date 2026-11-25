using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace JobFlow.Infrastructure.Migrations;

[DbContext(typeof(JobFlowDb))]
[Migration("20261123000000_ExternalSubmissionOption")]
public sealed class ExternalSubmissionOption : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddColumn<bool>("SubmitExternalApplications", "WorkflowDefinition", nullable: false, defaultValue: false);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropColumn("SubmitExternalApplications", "WorkflowDefinition");
}
