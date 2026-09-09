using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WCSBackend.Migrations
{
    /// <inheritdoc />
    public partial class PalletCodeManagement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "container_code",
                table: "wcs_slots",
                newName: "pallet_code");

            migrationBuilder.RenameIndex(
                name: "idx_slot_container",
                table: "wcs_slots",
                newName: "idx_slot_pallet");

            migrationBuilder.AddColumn<string>(
                name: "pallet_code",
                table: "wcs_containers",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "idx_cont_pallet",
                table: "wcs_containers",
                column: "pallet_code");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_cont_pallet",
                table: "wcs_containers");

            migrationBuilder.DropColumn(
                name: "pallet_code",
                table: "wcs_containers");

            migrationBuilder.RenameColumn(
                name: "pallet_code",
                table: "wcs_slots",
                newName: "container_code");

            migrationBuilder.RenameIndex(
                name: "idx_slot_pallet",
                table: "wcs_slots",
                newName: "idx_slot_container");
        }
    }
}
