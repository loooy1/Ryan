using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WCSBackend.Migrations
{
    /// <inheritdoc />
    public partial class SingleSlotInventory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "cargo_status",
                table: "wcs_slots",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "pallet_status",
                table: "wcs_slots",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            // 数据回填：wcs_containers 中 ready 且位于储位的容器，按储位合并写回储位行（托盘→pallet、货物→cargo）
            migrationBuilder.Sql("""
                UPDATE wcs_slots
                SET pallet_code = COALESCE((SELECT code FROM wcs_containers
                        WHERE slot_mark = wcs_slots.mark AND status = 'ready' AND type = 'Container' LIMIT 1), ''),
                    pallet_status = CASE WHEN EXISTS(SELECT 1 FROM wcs_containers
                        WHERE slot_mark = wcs_slots.mark AND status = 'ready' AND type = 'Container') THEN 'ready' ELSE '' END,
                    cargo_code = COALESCE((SELECT code FROM wcs_containers
                        WHERE slot_mark = wcs_slots.mark AND status = 'ready' AND type = 'Cargo' LIMIT 1), ''),
                    cargo_status = CASE WHEN EXISTS(SELECT 1 FROM wcs_containers
                        WHERE slot_mark = wcs_slots.mark AND status = 'ready' AND type = 'Cargo') THEN 'ready' ELSE '' END
                """);

            migrationBuilder.DropTable(
                name: "wcs_containers");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "cargo_status",
                table: "wcs_slots");

            migrationBuilder.DropColumn(
                name: "pallet_status",
                table: "wcs_slots");

            migrationBuilder.CreateTable(
                name: "wcs_containers",
                columns: table => new
                {
                    code = table.Column<string>(type: "TEXT", nullable: false),
                    home_mark = table.Column<string>(type: "TEXT", nullable: false),
                    pallet_code = table.Column<string>(type: "TEXT", nullable: false),
                    slot_mark = table.Column<string>(type: "TEXT", nullable: false),
                    status = table.Column<string>(type: "TEXT", nullable: false),
                    task_id = table.Column<string>(type: "TEXT", nullable: false),
                    type = table.Column<string>(type: "TEXT", nullable: false),
                    updated_at = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wcs_containers", x => x.code);
                });

            migrationBuilder.CreateIndex(
                name: "idx_cont_pallet",
                table: "wcs_containers",
                column: "pallet_code");

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
        }
    }
}
