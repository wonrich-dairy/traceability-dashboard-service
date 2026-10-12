using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TraceabilityService.Migrations
{
    /// <inheritdoc />
    public partial class AddDailyAggregateCheckpointDeviationCounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "IntakeDeviationCount",
                table: "daily_quality_aggregates",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "LabDeviationCount",
                table: "daily_quality_aggregates",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MccDeviationCount",
                table: "daily_quality_aggregates",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ProcessingDeviationCount",
                table: "daily_quality_aggregates",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_batch_records_Facility_FirstEventAt",
                table: "batch_records",
                columns: new[] { "Facility", "FirstEventAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_batch_records_Facility_FirstEventAt",
                table: "batch_records");

            migrationBuilder.DropColumn(
                name: "IntakeDeviationCount",
                table: "daily_quality_aggregates");

            migrationBuilder.DropColumn(
                name: "LabDeviationCount",
                table: "daily_quality_aggregates");

            migrationBuilder.DropColumn(
                name: "MccDeviationCount",
                table: "daily_quality_aggregates");

            migrationBuilder.DropColumn(
                name: "ProcessingDeviationCount",
                table: "daily_quality_aggregates");
        }
    }
}
