using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WCSBackend.Migrations;

/// <summary>Adds the WCS-owned artificial-sorting-station relation to each actual sorting slot.</summary>
public partial class AddSortingParentStation : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "parent_station_code",
            table: "wcs_slots",
            type: "TEXT",
            nullable: false,
            defaultValue: "");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "parent_station_code",
            table: "wcs_slots");
    }
}
