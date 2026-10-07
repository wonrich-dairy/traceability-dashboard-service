using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TraceabilityService.Migrations
{
    /// <inheritdoc />
    public partial class AddBatchTraceProductionDateStatusIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_batch_traces_ProductionDate_CurrentStatus",
                table: "batch_traces",
                columns: new[] { "ProductionDate", "CurrentStatus" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_batch_traces_ProductionDate_CurrentStatus",
                table: "batch_traces");
        }
    }
}
