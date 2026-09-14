using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WCSBackend.Migrations;

/// <summary>Adds the module HTTP pre-execution effect pipeline setting.</summary>
public partial class AddFeatureModulePreExecutionEffect : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "pre_execution_effect",
            table: "feature_modules",
            type: "TEXT",
            nullable: false,
            defaultValue: "");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "pre_execution_effect",
            table: "feature_modules");
    }
}
