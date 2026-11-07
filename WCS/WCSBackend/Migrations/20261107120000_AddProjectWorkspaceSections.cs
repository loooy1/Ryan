using Backend.Shared.Infrastructure;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace WCSBackend.Migrations;

[DbContext(typeof(GrcsDbContext))]
[Migration("20261107120000_AddProjectWorkspaceSections")]
public sealed class AddProjectWorkspaceSections : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "project_workspaces",
            columns: table => new
            {
                name = table.Column<string>(type: "varchar(191)", maxLength: 191, nullable: false)
                    .Annotation("MySql:CharSet", "utf8mb4"),
                created_at = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: false)
                    .Annotation("MySql:CharSet", "utf8mb4"),
                updated_at = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: false)
                    .Annotation("MySql:CharSet", "utf8mb4")
            },
            constraints: table => table.PrimaryKey("PK_project_workspaces", x => x.name))
            .Annotation("MySql:CharSet", "utf8mb4");

        migrationBuilder.CreateTable(
            name: "project_sections",
            columns: table => new
            {
                project_name = table.Column<string>(type: "varchar(191)", maxLength: 191, nullable: false)
                    .Annotation("MySql:CharSet", "utf8mb4"),
                section_key = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                    .Annotation("MySql:CharSet", "utf8mb4"),
                title = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false)
                    .Annotation("MySql:CharSet", "utf8mb4"),
                markdown_content = table.Column<string>(type: "longtext", nullable: false)
                    .Annotation("MySql:CharSet", "utf8mb4"),
                updated_at = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: false)
                    .Annotation("MySql:CharSet", "utf8mb4")
            },
            constraints: table => table.PrimaryKey("PK_project_sections", x => new { x.project_name, x.section_key }))
            .Annotation("MySql:CharSet", "utf8mb4");

        migrationBuilder.Sql("""
            INSERT IGNORE INTO project_workspaces (name, created_at, updated_at)
            SELECT DISTINCT TRIM(project), DATE_FORMAT(NOW(), '%Y-%m-%d %H:%i:%s'), DATE_FORMAT(NOW(), '%Y-%m-%d %H:%i:%s')
            FROM project_logs
            WHERE project IS NOT NULL AND TRIM(project) <> '';
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "project_sections");
        migrationBuilder.DropTable(name: "project_workspaces");
    }
}
