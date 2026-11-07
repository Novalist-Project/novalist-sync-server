using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Novalist.Sync.Server.Migrations
{
    /// <inheritdoc />
    public partial class HistoryBoundaryCreatedAtIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Repair databases that already applied EpochCompaction with the old constant default.
            migrationBuilder.Sql("ALTER TABLE project_epoch_stages ALTER COLUMN last_activity_at SET DEFAULT CURRENT_TIMESTAMP;");

            migrationBuilder.CreateIndex(
                name: "ix_project_history_boundaries_created_at",
                table: "project_history_boundaries",
                column: "created_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_project_history_boundaries_created_at",
                table: "project_history_boundaries");
        }
    }
}
