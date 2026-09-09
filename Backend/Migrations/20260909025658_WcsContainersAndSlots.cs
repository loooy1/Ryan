using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WCSBackend.Migrations
{
    /// <inheritdoc />
    public partial class WcsContainersAndSlots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "wcs_containers",
                columns: table => new
                {
                    code = table.Column<string>(type: "TEXT", nullable: false),
                    type = table.Column<string>(type: "TEXT", nullable: false),
                    status = table.Column<string>(type: "TEXT", nullable: false),
                    slot_mark = table.Column<string>(type: "TEXT", nullable: false),
                    task_id = table.Column<string>(type: "TEXT", nullable: false),
                    home_mark = table.Column<string>(type: "TEXT", nullable: false),
                    updated_at = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wcs_containers", x => x.code);
                });

            migrationBuilder.CreateTable(
                name: "wcs_slots",
                columns: table => new
                {
                    mark = table.Column<string>(type: "TEXT", nullable: false),
                    container_code = table.Column<string>(type: "TEXT", nullable: false),
                    cargo_code = table.Column<string>(type: "TEXT", nullable: false),
                    updated_at = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wcs_slots", x => x.mark);
                });

            migrationBuilder.CreateIndex(
                name: "idx_cont_slot",
                table: "wcs_containers",
                column: "slot_mark");

            migrationBuilder.CreateIndex(
                name: "idx_cont_status",
                table: "wcs_containers",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "idx_cont_task",
                table: "wcs_containers",
                column: "task_id");

            migrationBuilder.CreateIndex(
                name: "idx_slot_cargo",
                table: "wcs_slots",
                column: "cargo_code");

            migrationBuilder.CreateIndex(
                name: "idx_slot_container",
                table: "wcs_slots",
                column: "container_code");

            migrationBuilder.Sql(
                "INSERT INTO wcs_containers (code, type, status, slot_mark, task_id, home_mark, updated_at) " +
                "SELECT code, " +
                "CASE WHEN instr(upper(code), 'CARGO') > 0 THEN 'Cargo' ELSE 'Container' END, " +
                "CASE WHEN status = 'idle' THEN 'ready' " +
                "WHEN status = 'picked' THEN 'picked' " +
                "WHEN task_id LIKE 'fail:%' THEN 'fail' " +
                "WHEN task_id LIKE 'grcs_lock:%' THEN 'grcs_lock' " +
                "ELSE 'transit' END, " +
                "station, task_id, home_mark, updated_at " +
                "FROM wcs_inventory;");

            migrationBuilder.DropTable(
                name: "wcs_inventory");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "wcs_containers");

            migrationBuilder.DropTable(
                name: "wcs_slots");

            migrationBuilder.CreateTable(
                name: "wcs_inventory",
                columns: table => new
                {
                    code = table.Column<string>(type: "TEXT", nullable: false),
                    cargo_code = table.Column<string>(type: "TEXT", nullable: false),
                    home_mark = table.Column<string>(type: "TEXT", nullable: false),
                    station = table.Column<string>(type: "TEXT", nullable: false),
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
    }
}
