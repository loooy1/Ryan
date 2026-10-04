using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RCSBackend.Migrations
{
    /// <inheritdoc />
    public partial class AddRcsVehicleOperatingMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "rcs_system_mode");

            migrationBuilder.AddColumn<string>(
                name: "operating_mode",
                table: "rcs_vehicles",
                type: "varchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "AUTO")
                .Annotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "operating_mode",
                table: "rcs_vehicles");

            migrationBuilder.CreateTable(
                name: "rcs_system_mode",
                columns: table => new
                {
                    id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    mode = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    updated_at = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rcs_system_mode", x => x.id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");
        }
    }
}
