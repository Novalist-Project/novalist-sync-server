using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Novalist.Sync.Server.Migrations
{
    /// <inheritdoc />
    public partial class BlobChunks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "blob_chunks",
                columns: table => new
                {
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    blob_id = table.Column<Guid>(type: "uuid", nullable: false),
                    index = table.Column<int>(type: "integer", nullable: false),
                    ciphertext = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_blob_chunks", x => new { x.project_id, x.blob_id, x.index });
                    table.CheckConstraint("ck_blob_chunks_index", "index >= 0");
                    table.CheckConstraint("ck_blob_chunks_size", "octet_length(ciphertext) BETWEEN 16 AND 65552");
                    table.ForeignKey(
                        name: "fk_blob_chunks_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "blob_chunks");
        }
    }
}
