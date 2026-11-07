using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Novalist.Sync.Server.Migrations
{
    /// <inheritdoc />
    public partial class BlobStorageQuota : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "blob_bytes_stored",
                table: "projects",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.Sql("""
                UPDATE projects AS project
                SET blob_bytes_stored = usage.bytes
                FROM (
                    SELECT project_id, SUM(octet_length(ciphertext)) AS bytes
                    FROM blob_chunks GROUP BY project_id
                ) AS usage
                WHERE project.id = usage.project_id;
                """);

            migrationBuilder.AddCheckConstraint(
                name: "ck_projects_blob_bytes_stored",
                table: "projects",
                sql: "blob_bytes_stored >= 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_projects_blob_bytes_stored",
                table: "projects");

            migrationBuilder.DropColumn(
                name: "blob_bytes_stored",
                table: "projects");
        }
    }
}
