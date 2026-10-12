using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TraceabilityService.Migrations
{
    /// <inheritdoc />
    public partial class AddLabDeterminations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "lab_determinations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    DeterminationId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    BatchId = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Result = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    DeterminedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    DeterminedBy = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    SupersedesDeterminationId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    IsSuperseded = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    SupersededAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    TimelineEventId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lab_determinations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_lab_determinations_batch_records_BatchId",
                        column: x => x.BatchId,
                        principalTable: "batch_records",
                        principalColumn: "BatchId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_lab_determinations_timeline_events_TimelineEventId",
                        column: x => x.TimelineEventId,
                        principalTable: "timeline_events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_lab_determinations_BatchId_DeterminedAt",
                table: "lab_determinations",
                columns: new[] { "BatchId", "DeterminedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_lab_determinations_DeterminationId",
                table: "lab_determinations",
                column: "DeterminationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_lab_determinations_SupersedesDeterminationId",
                table: "lab_determinations",
                column: "SupersedesDeterminationId");

            migrationBuilder.CreateIndex(
                name: "IX_lab_determinations_TimelineEventId",
                table: "lab_determinations",
                column: "TimelineEventId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "lab_determinations");
        }
    }
}
