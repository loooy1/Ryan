using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RCSBackend.Migrations
{
    /// <inheritdoc />
    public partial class AddRcsTaskExecutionStages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "execution_stages_json",
                table: "rcs_tasks",
                type: "longtext",
                nullable: false,
                defaultValue: "[]")
                .Annotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "execution_stages_json",
                table: "rcs_tasks");
        }
    }
}
