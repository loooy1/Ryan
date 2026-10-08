using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WCSBackend.Migrations
{
    /// <inheritdoc />
    public partial class UnifyTaskAndFeatureTemplates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "template_kind",
                table: "feature_modules");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "template_kind",
                table: "feature_modules",
                type: "varchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "module")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.Sql(@"
UPDATE `feature_modules`
SET `template_kind` = 'task_protocol'
WHERE `module_id` IN ('task.carry', 'task.move') OR `module_id` LIKE 'custom.%';");
        }
    }
}
