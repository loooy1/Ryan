using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RCSBackend.Migrations
{
    /// <inheritdoc />
    public partial class AddRcsTaskStationActions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "station_actions_json",
                table: "rcs_tasks",
                type: "longtext",
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.Sql("UPDATE rcs_tasks SET station_actions_json = '[]' WHERE station_actions_json IS NULL;");

            migrationBuilder.AlterColumn<string>(
                name: "station_actions_json",
                table: "rcs_tasks",
                type: "longtext",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "longtext",
                oldNullable: true)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "station_actions_json",
                table: "rcs_tasks");
        }
    }
}
