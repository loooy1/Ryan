using Backend.Shared.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WCSBackend.Migrations;

[DbContext(typeof(GrcsDbContext))]
[Migration("20260910205000_RenameInventoryUnavailableToDestinationUnavailable")]
public partial class RenameInventoryUnavailableToDestinationUnavailable : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            UPDATE wcs_slots
            SET selection_status = CASE
                WHEN task_lock_id <> '' AND selection_status = 'task_end_locked' THEN 'task_end_locked'
                WHEN task_lock_id <> '' THEN 'task_start_locked'
                WHEN site_type <> 'Storage' THEN 'available'
                WHEN (pallet_code <> '' AND pallet_status = 'ready')
                  OR (cargo_code <> '' AND cargo_status = 'ready') THEN 'destination_unavailable'
                WHEN (pallet_code <> '' AND pallet_status <> 'transit')
                  OR (cargo_code <> '' AND cargo_status <> 'transit') THEN 'destination_unavailable'
                ELSE 'start_unavailable'
            END;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            UPDATE wcs_slots
            SET selection_status = CASE
                WHEN selection_status = 'destination_unavailable' THEN 'inventory_unavailable'
                ELSE selection_status
            END;
            """);
    }
}
