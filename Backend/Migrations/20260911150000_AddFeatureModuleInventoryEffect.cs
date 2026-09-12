using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WCSBackend.Migrations;

/// <summary>Lets each feature module declare its successful inventory action.</summary>
public partial class AddFeatureModuleInventoryEffect : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "inventory_effect",
            table: "feature_modules",
            type: "TEXT",
            nullable: false,
            defaultValue: "");

        // Preserve current module behaviour once when an existing database is upgraded.
        migrationBuilder.Sql("""
            UPDATE "feature_modules"
            SET "inventory_effect" = 'cargo_arrival'
            WHERE lower(trim("api_url")) = '/api/v1/container_ready';
            """);
        migrationBuilder.Sql("""
            UPDATE "feature_modules"
            SET "inventory_effect" = 'cargo_removal'
            WHERE lower(trim("api_url")) = '/api/v1/container_remove';
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE "__feature_modules_old" (
                "module_id" TEXT NOT NULL CONSTRAINT "PK_feature_modules" PRIMARY KEY,
                "name" TEXT NOT NULL,
                "api_url" TEXT NOT NULL,
                "params_json" TEXT NOT NULL,
                "updated_at" TEXT NOT NULL
            );
            """);
        migrationBuilder.Sql("""
            INSERT INTO "__feature_modules_old" ("module_id", "name", "api_url", "params_json", "updated_at")
            SELECT "module_id", "name", "api_url", "params_json", "updated_at"
            FROM "feature_modules";
            """);
        migrationBuilder.Sql("""DROP TABLE "feature_modules";""");
        migrationBuilder.Sql("""ALTER TABLE "__feature_modules_old" RENAME TO "feature_modules";""");
    }
}
