using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Novalist.Sync.Server.Migrations
{
    /// <inheritdoc />
    public partial class BlobCollection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "blobs",
                columns: table => new
                {
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    blob_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bytes_stored = table.Column<long>(type: "bigint", nullable: false),
                    unreferenced_since = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    collectable_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_blobs", x => new { x.project_id, x.blob_id });
                    table.CheckConstraint("ck_blobs_bytes_stored", "bytes_stored >= 0");
                    table.CheckConstraint("ck_blobs_collectable", "collectable_at IS NULL OR unreferenced_since IS NOT NULL");
                    table.ForeignKey(
                        name: "fk_blobs_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_blobs_collectable",
                table: "blobs",
                column: "project_id",
                filter: "collectable_at IS NOT NULL");

            migrationBuilder.Sql("""
                INSERT INTO blobs (project_id, blob_id, bytes_stored)
                SELECT project_id, blob_id, SUM(octet_length(ciphertext))
                FROM blob_chunks GROUP BY project_id, blob_id;
                """);

            migrationBuilder.AddForeignKey(
                name: "fk_blob_chunks_blobs_project_id_blob_id",
                table: "blob_chunks",
                columns: new[] { "project_id", "blob_id" },
                principalTable: "blobs",
                principalColumns: new[] { "project_id", "blob_id" },
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_blob_chunks_blobs_project_id_blob_id",
                table: "blob_chunks");

            migrationBuilder.DropTable(
                name: "blobs");
        }
    }
}
