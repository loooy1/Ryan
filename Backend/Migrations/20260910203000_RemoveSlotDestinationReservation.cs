using Backend.Shared.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WCSBackend.Migrations;

[DbContext(typeof(GrcsDbContext))]
[Migration("20260910203000_RemoveSlotDestinationReservation")]
public partial class RemoveSlotDestinationReservation : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE "wcs_slots_new" (
                "mark" TEXT NOT NULL CONSTRAINT "PK_wcs_slots" PRIMARY KEY,
                "site_type" TEXT NOT NULL DEFAULT '',
                "pallet_code" TEXT NOT NULL,
                "pallet_status" TEXT NOT NULL DEFAULT '',
                "cargo_code" TEXT NOT NULL,
                "cargo_status" TEXT NOT NULL DEFAULT '',
                "task_lock_id" TEXT NOT NULL DEFAULT '',
                "selection_status" TEXT NOT NULL DEFAULT 'available',
                "updated_at" TEXT NOT NULL
            );
            INSERT INTO "wcs_slots_new" (
                mark, site_type, pallet_code, pallet_status, cargo_code, cargo_status,
                task_lock_id, selection_status, updated_at)
            SELECT
                mark, site_type, pallet_code, pallet_status, cargo_code, cargo_status,
                CASE WHEN task_lock_id <> '' THEN task_lock_id ELSE reserved_task_id END,
                selection_status, updated_at
            FROM "wcs_slots";
            DROP TABLE "wcs_slots";
            ALTER TABLE "wcs_slots_new" RENAME TO "wcs_slots";
            CREATE INDEX "idx_slot_pallet" ON "wcs_slots" ("pallet_code");
            CREATE INDEX "idx_slot_cargo" ON "wcs_slots" ("cargo_code");
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE "wcs_slots_new" (
                "mark" TEXT NOT NULL CONSTRAINT "PK_wcs_slots" PRIMARY KEY,
                "site_type" TEXT NOT NULL DEFAULT '',
                "pallet_code" TEXT NOT NULL,
                "pallet_status" TEXT NOT NULL DEFAULT '',
                "cargo_code" TEXT NOT NULL,
                "cargo_status" TEXT NOT NULL DEFAULT '',
                "reserved_task_id" TEXT NOT NULL DEFAULT '',
                "task_lock_id" TEXT NOT NULL DEFAULT '',
                "selection_status" TEXT NOT NULL DEFAULT 'available',
                "updated_at" TEXT NOT NULL
            );
            INSERT INTO "wcs_slots_new" (
                mark, site_type, pallet_code, pallet_status, cargo_code, cargo_status,
                reserved_task_id, task_lock_id, selection_status, updated_at)
            SELECT
                mark, site_type, pallet_code, pallet_status, cargo_code, cargo_status,
                CASE WHEN selection_status = 'task_end_locked' THEN task_lock_id ELSE '' END,
                task_lock_id, selection_status, updated_at
            FROM "wcs_slots";
            DROP TABLE "wcs_slots";
            ALTER TABLE "wcs_slots_new" RENAME TO "wcs_slots";
            CREATE INDEX "idx_slot_pallet" ON "wcs_slots" ("pallet_code");
            CREATE INDEX "idx_slot_cargo" ON "wcs_slots" ("cargo_code");
            """);
    }
}
