using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GrcsBackend.Migrations
{
    /// <inheritdoc />
    public partial class WcsInventory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "wcs_inventory",
                columns: table => new
                {
                    code = table.Column<string>(type: "TEXT", nullable: false),
                    station = table.Column<string>(type: "TEXT", nullable: false),
                    home_mark = table.Column<string>(type: "TEXT", nullable: false),
                    cargo_code = table.Column<string>(type: "TEXT", nullable: false),
                    status = table.Column<string>(type: "TEXT", nullable: false),
                    task_id = table.Column<string>(type: "TEXT", nullable: false),
                    updated_at = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wcs_inventory", x => x.code);
                });

            migrationBuilder.CreateIndex(
                name: "idx_inv_status",
                table: "wcs_inventory",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "idx_inv_task",
                table: "wcs_inventory",
                column: "task_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "wcs_inventory");
        }
    }
}
