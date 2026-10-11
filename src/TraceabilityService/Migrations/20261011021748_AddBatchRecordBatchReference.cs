using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TraceabilityService.Migrations
{
    /// <inheritdoc />
    public partial class AddBatchRecordBatchReference : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BatchReference",
                table: "batch_records",
                type: "varchar(50)",
                maxLength: 50,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_batch_records_BatchReference",
                table: "batch_records",
                column: "BatchReference");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_batch_records_BatchReference",
                table: "batch_records");

            migrationBuilder.DropColumn(
                name: "BatchReference",
                table: "batch_records");
        }
    }
}
