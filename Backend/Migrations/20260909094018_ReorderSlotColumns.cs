using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WCSBackend.Migrations
{
    /// <inheritdoc />
    public partial class ReorderSlotColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // SQLite 不支持改列序，重建表：mark, site_type, pallet_code, pallet_status, cargo_code, cargo_status, updated_at
            migrationBuilder.Sql("""
                CREATE TABLE "wcs_slots_new" (
                    "mark" TEXT NOT NULL CONSTRAINT "PK_wcs_slots" PRIMARY KEY,
                    "site_type" TEXT NOT NULL DEFAULT '',
                    "pallet_code" TEXT NOT NULL,
                    "pallet_status" TEXT NOT NULL DEFAULT '',
                    "cargo_code" TEXT NOT NULL,
                    "cargo_status" TEXT NOT NULL DEFAULT '',
                    "updated_at" TEXT NOT NULL
                );
                INSERT INTO "wcs_slots_new" (mark, site_type, pallet_code, pallet_status, cargo_code, cargo_status, updated_at)
                SELECT mark, site_type, pallet_code, pallet_status, cargo_code, cargo_status, updated_at FROM "wcs_slots";
                DROP TABLE "wcs_slots";
                ALTER TABLE "wcs_slots_new" RENAME TO "wcs_slots";
                CREATE INDEX "idx_slot_pallet" ON "wcs_slots" ("pallet_code");
                CREATE INDEX "idx_slot_cargo" ON "wcs_slots" ("cargo_code");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // 反向：恢复旧列序（mark, pallet_code, cargo_code, updated_at, cargo_status, pallet_status, site_type）
            migrationBuilder.Sql("""
                CREATE TABLE "wcs_slots_new" (
                    "mark" TEXT NOT NULL CONSTRAINT "PK_wcs_slots" PRIMARY KEY,
                    "pallet_code" TEXT NOT NULL,
                    "cargo_code" TEXT NOT NULL,
                    "updated_at" TEXT NOT NULL,
                    "cargo_status" TEXT NOT NULL DEFAULT '',
                    "pallet_status" TEXT NOT NULL DEFAULT '',
                    "site_type" TEXT NOT NULL DEFAULT ''
                );
                INSERT INTO "wcs_slots_new" (mark, pallet_code, cargo_code, updated_at, cargo_status, pallet_status, site_type)
                SELECT mark, pallet_code, cargo_code, updated_at, cargo_status, pallet_status, site_type FROM "wcs_slots";
                DROP TABLE "wcs_slots";
                ALTER TABLE "wcs_slots_new" RENAME TO "wcs_slots";
                CREATE INDEX "idx_slot_pallet" ON "wcs_slots" ("pallet_code");
                CREATE INDEX "idx_slot_cargo" ON "wcs_slots" ("cargo_code");
                """);
        }
    }
}