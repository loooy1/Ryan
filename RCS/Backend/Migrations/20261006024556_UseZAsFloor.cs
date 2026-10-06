using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RCSBackend.Migrations
{
    /// <inheritdoc />
    public partial class UseZAsFloor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Preserve the existing level before removing the redundant floor column.
            migrationBuilder.Sql("UPDATE `rcs_map_points` SET `z` = `floor`;");
            migrationBuilder.DropColumn(
                name: "floor",
                table: "rcs_map_points");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "floor",
                table: "rcs_map_points",
                type: "int",
                nullable: false,
                defaultValue: 0);
            migrationBuilder.Sql("UPDATE `rcs_map_points` SET `floor` = CAST(`z` AS SIGNED);");
        }
    }
}
