using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WCSBackend.Migrations
{
    /// <inheritdoc />
    public partial class AddSlotMapCoordinates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "floor",
                table: "wcs_slots",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<double>(
                name: "x",
                table: "wcs_slots",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "y",
                table: "wcs_slots",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "floor",
                table: "wcs_slots");

            migrationBuilder.DropColumn(
                name: "x",
                table: "wcs_slots");

            migrationBuilder.DropColumn(
                name: "y",
                table: "wcs_slots");
        }
    }
}
