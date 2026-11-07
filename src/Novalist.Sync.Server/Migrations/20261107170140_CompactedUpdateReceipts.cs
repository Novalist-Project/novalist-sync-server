using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Novalist.Sync.Server.Migrations
{
    /// <inheritdoc />
    public partial class CompactedUpdateReceipts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "update_receipts",
                columns: table => new
                {
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sender_device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sender_seq = table.Column<long>(type: "bigint", nullable: false),
                    server_seq = table.Column<long>(type: "bigint", nullable: false),
                    header_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    nonce_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    ciphertext_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    signature_hash = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_update_receipts", x => new { x.project_id, x.sender_device_id, x.sender_seq });
                    table.ForeignKey(
                        name: "fk_update_receipts_devices_sender_device_id",
                        column: x => x.sender_device_id,
                        principalTable: "devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_update_receipts_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_update_receipts_sender_device_id",
                table: "update_receipts",
                column: "sender_device_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "update_receipts");
        }
    }
}
