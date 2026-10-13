using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TraceabilityService.Migrations
{
    /// <summary>
    /// SCRUM-138: audit_entries becomes append-only at the database, whatever the client: an UPDATE
    /// or DELETE on it fails with SQLSTATE 45000. TRUNCATE and DROP fire no trigger, but need
    /// privileges a deployed app has no reason to use. Also indexes the audit trail's user filter.
    /// Creating a trigger needs the TRIGGER privilege, and on a server with binary logging (Azure)
    /// log_bin_trust_function_creators = ON; see docs/database.md. Trigger statements here must be
    /// safe to run twice: see TriggerAwareMySqlMigrator.
    /// </summary>
    public partial class MakeAuditEntriesAppendOnly : Migration
    {
        public const string NoUpdateTrigger = "audit_entries_no_update";
        public const string NoDeleteTrigger = "audit_entries_no_delete";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_timeline_events_RecordedBy_OccurredAt",
                table: "timeline_events",
                columns: new[] { "RecordedBy", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_audit_entries_Actor_OccurredAt",
                table: "audit_entries",
                columns: new[] { "Actor", "OccurredAt" });

            // One statement each, so no BEGIN ... END and no DELIMITER in the migration script. IF NOT
            // EXISTS (MySQL 8.0.29+) because the idempotent script runs them on every deploy, outside
            // its guards (TriggerAwareMySqlMigrator).
            migrationBuilder.Sql($"""
                CREATE TRIGGER IF NOT EXISTS {NoUpdateTrigger} BEFORE UPDATE ON audit_entries FOR EACH ROW
                SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'audit_entries is append-only: an entry cannot be changed.';
                """);

            migrationBuilder.Sql($"""
                CREATE TRIGGER IF NOT EXISTS {NoDeleteTrigger} BEFORE DELETE ON audit_entries FOR EACH ROW
                SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'audit_entries is append-only: an entry cannot be deleted.';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($"DROP TRIGGER IF EXISTS {NoDeleteTrigger};");

            migrationBuilder.Sql($"DROP TRIGGER IF EXISTS {NoUpdateTrigger};");

            migrationBuilder.DropIndex(
                name: "IX_timeline_events_RecordedBy_OccurredAt",
                table: "timeline_events");

            migrationBuilder.DropIndex(
                name: "IX_audit_entries_Actor_OccurredAt",
                table: "audit_entries");
        }
    }
}
