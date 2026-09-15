using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WCSBackend.Migrations;

public partial class AddModuleExecutionLogs : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "module_exec_logs",
            columns: table => new
            {
                id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                task_id = table.Column<string>(type: "TEXT", nullable: false),
                module_id = table.Column<string>(type: "TEXT", nullable: false),
                module_name = table.Column<string>(type: "TEXT", nullable: false),
                execution_phase = table.Column<string>(type: "TEXT", nullable: false),
                status = table.Column<string>(type: "TEXT", nullable: false),
                http_status = table.Column<int>(type: "INTEGER", nullable: false),
                detail_json = table.Column<string>(type: "TEXT", nullable: false),
                started_at = table.Column<string>(type: "TEXT", nullable: false),
                finished_at = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_module_exec_logs", x => x.id));

        migrationBuilder.CreateIndex(
            name: "idx_module_exec_logs_task_time",
            table: "module_exec_logs",
            columns: new[] { "task_id", "started_at" });
        migrationBuilder.CreateIndex(
            name: "idx_module_exec_logs_started_at",
            table: "module_exec_logs",
            column: "started_at");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.DropTable(name: "module_exec_logs");
}
