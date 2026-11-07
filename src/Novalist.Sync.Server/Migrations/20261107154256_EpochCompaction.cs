using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Novalist.Sync.Server.Migrations
{
    /// <inheritdoc />
    public partial class EpochCompaction : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "history_starts_at",
                table: "projects",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_activity_at",
                table: "project_epoch_stages",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            // In-flight reservations keep their original start as their last activity.
            migrationBuilder.Sql("UPDATE project_epoch_stages SET last_activity_at = created_at;");

            migrationBuilder.CreateTable(
                name: "project_history_boundaries",
                columns: table => new
                {
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    first_server_seq = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_history_boundaries", x => new { x.project_id, x.first_server_seq });
                    table.ForeignKey(
                        name: "fk_project_history_boundaries_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Projects that already rotated: history starts at the newest grant boundary, and every earlier
            // boundary gets a fresh timestamp so the grace period starts with this upgrade.
            migrationBuilder.Sql("""
                UPDATE projects p SET history_starts_at = g.first_server_seq
                FROM (SELECT project_id, max(first_server_seq) AS first_server_seq FROM project_epoch_key_grants GROUP BY project_id) g
                WHERE g.project_id = p.id;
                INSERT INTO project_history_boundaries (project_id, first_server_seq, created_at)
                SELECT DISTINCT project_id, first_server_seq, now() FROM project_epoch_key_grants;
                """);

            migrationBuilder.AddCheckConstraint(
                name: "ck_projects_history_starts_at",
                table: "projects",
                sql: "history_starts_at >= 1");

            migrationBuilder.CreateIndex(
                name: "ix_project_epoch_stages_last_activity_at",
                table: "project_epoch_stages",
                column: "last_activity_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "project_history_boundaries");

            migrationBuilder.DropCheckConstraint(
                name: "ck_projects_history_starts_at",
                table: "projects");

            migrationBuilder.DropIndex(
                name: "ix_project_epoch_stages_last_activity_at",
                table: "project_epoch_stages");

            migrationBuilder.DropColumn(
                name: "history_starts_at",
                table: "projects");

            migrationBuilder.DropColumn(
                name: "last_activity_at",
                table: "project_epoch_stages");
        }
    }
}
