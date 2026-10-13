using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TraceabilityService.Migrations
{
    /// <inheritdoc />
    public partial class AddUpstreamSnapshotRetryTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LastCorrelationId",
                table: "upstream_snapshots",
                type: "varchar(64)",
                maxLength: 64,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<DateTime>(
                name: "NextAttemptAt",
                table: "upstream_snapshots",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_upstream_snapshots_ResolutionStatus_NextAttemptAt",
                table: "upstream_snapshots",
                columns: new[] { "ResolutionStatus", "NextAttemptAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_upstream_snapshots_ResolutionStatus_NextAttemptAt",
                table: "upstream_snapshots");

            migrationBuilder.DropColumn(
                name: "LastCorrelationId",
                table: "upstream_snapshots");

            migrationBuilder.DropColumn(
                name: "NextAttemptAt",
                table: "upstream_snapshots");
        }
    }
}
