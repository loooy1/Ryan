using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WCSBackend.Migrations;

/// <summary>模块执行、在途与信号确认改由 task_records 记录。</summary>
public partial class ConsolidateTaskRecords : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "module_exec_logs");
        migrationBuilder.DropTable(name: "workflow_state");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "module_exec_logs",
            columns: table => new
            {
                id = table.Column<long>(type: "INTEGER", nullable: false).Annotation("Sqlite:Autoincrement", true),
                task_id = table.Column<string>(type: "TEXT", nullable: false),
                point = table.Column<string>(type: "TEXT", nullable: false),
                module = table.Column<string>(type: "TEXT", nullable: false),
                ok = table.Column<bool>(type: "INTEGER", nullable: false),
                http_code = table.Column<int>(type: "INTEGER", nullable: false),
                detail = table.Column<string>(type: "TEXT", nullable: false),
                created_at = table.Column<string>(type: "TEXT", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_module_exec_logs", x => x.id));

        migrationBuilder.CreateTable(
            name: "workflow_state",
            columns: table => new
            {
                kind = table.Column<string>(type: "TEXT", nullable: false),
                task_id = table.Column<string>(type: "TEXT", nullable: false),
                value = table.Column<string>(type: "TEXT", nullable: true),
                time = table.Column<string>(type: "TEXT", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_workflow_state", x => new { x.kind, x.task_id }));
    }
}
