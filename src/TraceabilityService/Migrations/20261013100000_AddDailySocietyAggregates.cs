using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using TraceabilityService.Api.Infrastructure.Persistence;

#nullable disable

namespace TraceabilityService.Migrations;

[DbContext(typeof(TraceabilityDbContext))]
[Migration("20261013100000_AddDailySocietyAggregates")]
public sealed class AddDailySocietyAggregates : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "daily_society_aggregates",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false)
                    .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                Date = table.Column<DateOnly>(type: "date", nullable: false),
                SocietyCode = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: false)
                    .Annotation("MySql:CharSet", "utf8mb4"),
                SocietyName = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true)
                    .Annotation("MySql:CharSet", "utf8mb4"),
                TotalConsignments = table.Column<int>(type: "int", nullable: false),
                AcceptedCount = table.Column<int>(type: "int", nullable: false),
                RejectedCount = table.Column<int>(type: "int", nullable: false),
                FatPercentTotal = table.Column<decimal>(type: "decimal(12,4)", precision: 12, scale: 4, nullable: false),
                SnfTotal = table.Column<decimal>(type: "decimal(12,4)", precision: 12, scale: 4, nullable: false),
                MarginalCount = table.Column<int>(type: "int", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_daily_society_aggregates", x => x.Id))
            .Annotation("MySql:CharSet", "utf8mb4");

        migrationBuilder.CreateIndex(
            name: "IX_daily_society_aggregates_Date_SocietyCode",
            table: "daily_society_aggregates",
            columns: new[] { "Date", "SocietyCode" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "daily_society_aggregates");
    }
}
