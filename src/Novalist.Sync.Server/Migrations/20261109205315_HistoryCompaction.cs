using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Novalist.Sync.Server.Migrations
{
    /// <inheritdoc />
    public partial class HistoryCompaction : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "checkpoint_bytes",
                table: "project_history_boundaries",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "compaction",
                table: "project_epoch_stages",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "checkpoint_bytes",
                table: "project_history_boundaries");

            migrationBuilder.DropColumn(
                name: "compaction",
                table: "project_epoch_stages");
        }
    }
}
