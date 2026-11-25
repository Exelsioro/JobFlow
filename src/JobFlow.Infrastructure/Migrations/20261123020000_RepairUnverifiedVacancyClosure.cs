using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace JobFlow.Infrastructure.Migrations;

[DbContext(typeof(JobFlowDb))]
[Migration("20261123020000_RepairUnverifiedVacancyClosure")]
public sealed class RepairUnverifiedVacancyClosure : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        UPDATE "Vacancies" AS vacancy
        SET "Availability" = 'Unknown'
        WHERE vacancy."Availability" = 'Closed'
          AND EXISTS (
              SELECT 1
              FROM "Audit" AS evidence
              WHERE evidence."Id" = (
                  SELECT latest."Id"
                  FROM "Audit" AS latest
                  WHERE latest."EntityId" = vacancy."Id" AND latest."Action" = 'VacancyFreshnessChecked'
                  ORDER BY latest."CreatedAt" DESC, latest."Id" DESC
                  LIMIT 1
              )
              AND evidence."CreatedAt" >= vacancy."VerifiedAt"
              AND evidence."Details" LIKE '%"availability":"Closed"%'
              AND evidence."Details" LIKE '%Open-status evidence is not an exact nonempty source quote.%'
          );
        """);

    // An unverified closure should not be restored on downgrade.
    protected override void Down(MigrationBuilder migrationBuilder) { }
}
