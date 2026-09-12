using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WCSBackend.Migrations;

/// <summary>Separates feature-module task pallet and cargo parameter sources.</summary>
public partial class AddPalletAndCargoModuleSources : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Existing container_ready and container_remove modules both use the second
        // parameter as ContainerCode. They are cargo signals, so keep them aligned.
        migrationBuilder.Sql("""
            UPDATE "feature_modules"
            SET "params_json" = json_set("params_json", '$[1].Source', 9)
            WHERE lower(trim("api_url")) IN ('/api/v1/container_ready', '/api/v1/container_remove')
              AND json_extract("params_json", '$[1].Name') = 'ContainerCode'
              AND json_extract("params_json", '$[1].Source') = 3;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            UPDATE "feature_modules"
            SET "params_json" = json_set("params_json", '$[1].Source', 3)
            WHERE lower(trim("api_url")) IN ('/api/v1/container_ready', '/api/v1/container_remove')
              AND json_extract("params_json", '$[1].Name') = 'ContainerCode'
              AND json_extract("params_json", '$[1].Source') = 9;
            """);
    }
}
