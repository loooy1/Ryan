using Backend.Shared.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WCSBackend.Migrations;

[DbContext(typeof(GrcsDbContext))]
[Migration("20260910201000_AddSlotSelectionStatus")]
public partial class AddSlotSelectionStatus : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "selection_status",
            table: "wcs_slots",
            type: "TEXT",
            nullable: false,
            defaultValue: "available");

        migrationBuilder.Sql("""
            UPDATE wcs_slots
            SET selection_status = CASE
                WHEN reserved_task_id <> '' THEN 'task_end_locked'
                WHEN task_lock_id <> '' THEN 'task_start_locked'
                WHEN (pallet_code <> '' AND pallet_status <> 'transit')
                  OR (cargo_code <> '' AND cargo_status <> 'transit') THEN 'inventory_unavailable'
                ELSE 'available'
            END;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "selection_status",
            table: "wcs_slots");
    }
}
