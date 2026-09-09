using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WCSBackend.Migrations
{
    /// <inheritdoc />
    public partial class AllBusinessTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "auto_templates",
                columns: table => new
                {
                    template_id = table.Column<string>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    steps_json = table.Column<string>(type: "TEXT", nullable: false),
                    updated_at = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_auto_templates", x => x.template_id);
                });

            migrationBuilder.CreateTable(
                name: "exception_records",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    happened_at = table.Column<string>(type: "TEXT", nullable: false),
                    vehicle_code = table.Column<string>(type: "TEXT", nullable: true),
                    phenomenon = table.Column<string>(type: "TEXT", nullable: false),
                    reason = table.Column<string>(type: "TEXT", nullable: false),
                    progress = table.Column<string>(type: "TEXT", nullable: true),
                    responsible_dept = table.Column<string>(type: "TEXT", nullable: false),
                    status = table.Column<string>(type: "TEXT", nullable: false),
                    project = table.Column<string>(type: "TEXT", nullable: false),
                    reproduced_at = table.Column<string>(type: "TEXT", nullable: true),
                    reproduce_count = table.Column<int>(type: "INTEGER", nullable: false),
                    created_at = table.Column<string>(type: "TEXT", nullable: false),
                    updated_at = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_exception_records", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "feature_modules",
                columns: table => new
                {
                    module_id = table.Column<string>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    api_url = table.Column<string>(type: "TEXT", nullable: false),
                    params_json = table.Column<string>(type: "TEXT", nullable: false),
                    updated_at = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_feature_modules", x => x.module_id);
                });

            migrationBuilder.CreateTable(
                name: "kv",
                columns: table => new
                {
                    key = table.Column<string>(type: "TEXT", nullable: false),
                    value = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_kv", x => x.key);
                });

            migrationBuilder.CreateTable(
                name: "mock_request_events",
                columns: table => new
                {
                    event_key = table.Column<string>(type: "TEXT", nullable: false),
                    event_id = table.Column<long>(type: "INTEGER", nullable: false),
                    path_pattern = table.Column<string>(type: "TEXT", nullable: false),
                    method = table.Column<string>(type: "TEXT", nullable: false),
                    body_json = table.Column<string>(type: "TEXT", nullable: false),
                    query_string = table.Column<string>(type: "TEXT", nullable: false),
                    time = table.Column<string>(type: "TEXT", nullable: false),
                    decided_at = table.Column<string>(type: "TEXT", nullable: true),
                    status = table.Column<string>(type: "TEXT", nullable: false),
                    attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    mock_rule_id = table.Column<string>(type: "TEXT", nullable: false),
                    mock_rule_desc = table.Column<string>(type: "TEXT", nullable: false),
                    rule_json = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mock_request_events", x => x.event_key);
                });

            migrationBuilder.CreateTable(
                name: "mock_rules",
                columns: table => new
                {
                    rule_id = table.Column<string>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    method = table.Column<string>(type: "TEXT", nullable: false),
                    path_pattern = table.Column<string>(type: "TEXT", nullable: false),
                    matchers_json = table.Column<string>(type: "TEXT", nullable: false),
                    response_code = table.Column<int>(type: "INTEGER", nullable: false),
                    response_body = table.Column<string>(type: "TEXT", nullable: false),
                    enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    priority = table.Column<int>(type: "INTEGER", nullable: false),
                    description = table.Column<string>(type: "TEXT", nullable: false),
                    also_record = table.Column<bool>(type: "INTEGER", nullable: false),
                    board_sync = table.Column<bool>(type: "INTEGER", nullable: false),
                    requires_approval = table.Column<bool>(type: "INTEGER", nullable: false),
                    approval_variable = table.Column<string>(type: "TEXT", nullable: false),
                    approval_true_value = table.Column<string>(type: "TEXT", nullable: false),
                    approval_false_value = table.Column<string>(type: "TEXT", nullable: false),
                    updated_at = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mock_rules", x => x.rule_id);
                });

            migrationBuilder.CreateTable(
                name: "module_exec_logs",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    task_id = table.Column<string>(type: "TEXT", nullable: false),
                    point = table.Column<string>(type: "TEXT", nullable: false),
                    module = table.Column<string>(type: "TEXT", nullable: false),
                    ok = table.Column<bool>(type: "INTEGER", nullable: false),
                    http_code = table.Column<int>(type: "INTEGER", nullable: false),
                    detail = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_module_exec_logs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "project_logs",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    log_date = table.Column<string>(type: "TEXT", nullable: false),
                    content = table.Column<string>(type: "TEXT", nullable: false),
                    status = table.Column<string>(type: "TEXT", nullable: false),
                    project = table.Column<string>(type: "TEXT", nullable: false),
                    remark = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<string>(type: "TEXT", nullable: false),
                    updated_at = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_logs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "task_records",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    task_id = table.Column<string>(type: "TEXT", nullable: false),
                    stage = table.Column<string>(type: "TEXT", nullable: false),
                    time = table.Column<string>(type: "TEXT", nullable: false),
                    warehouse = table.Column<string>(type: "TEXT", nullable: false),
                    container_code = table.Column<string>(type: "TEXT", nullable: false),
                    cargo_code = table.Column<string>(type: "TEXT", nullable: false),
                    task_type = table.Column<string>(type: "TEXT", nullable: false),
                    route_codes = table.Column<string>(type: "TEXT", nullable: false),
                    station_code = table.Column<string>(type: "TEXT", nullable: false),
                    ok = table.Column<bool>(type: "INTEGER", nullable: false),
                    status_code = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_task_records", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "task_templates",
                columns: table => new
                {
                    value = table.Column<string>(type: "TEXT", nullable: false),
                    label = table.Column<string>(type: "TEXT", nullable: false),
                    description = table.Column<string>(type: "TEXT", nullable: false),
                    category = table.Column<string>(type: "TEXT", nullable: false),
                    needs_container = table.Column<bool>(type: "INTEGER", nullable: false),
                    container_prefix = table.Column<string>(type: "TEXT", nullable: false),
                    random_container = table.Column<bool>(type: "INTEGER", nullable: false),
                    start_json = table.Column<string>(type: "TEXT", nullable: false),
                    end_json = table.Column<string>(type: "TEXT", nullable: false),
                    updated_at = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_task_templates", x => x.value);
                });

            migrationBuilder.CreateTable(
                name: "workflow_state",
                columns: table => new
                {
                    kind = table.Column<string>(type: "TEXT", nullable: false),
                    task_id = table.Column<string>(type: "TEXT", nullable: false),
                    value = table.Column<string>(type: "TEXT", nullable: true),
                    time = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_state", x => new { x.kind, x.task_id });
                });

            migrationBuilder.CreateIndex(
                name: "idx_tr_stage",
                table: "task_records",
                column: "stage");

            migrationBuilder.CreateIndex(
                name: "idx_tr_task",
                table: "task_records",
                column: "task_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "auto_templates");

            migrationBuilder.DropTable(
                name: "exception_records");

            migrationBuilder.DropTable(
                name: "feature_modules");

            migrationBuilder.DropTable(
                name: "kv");

            migrationBuilder.DropTable(
                name: "mock_request_events");

            migrationBuilder.DropTable(
                name: "mock_rules");

            migrationBuilder.DropTable(
                name: "module_exec_logs");

            migrationBuilder.DropTable(
                name: "project_logs");

            migrationBuilder.DropTable(
                name: "task_records");

            migrationBuilder.DropTable(
                name: "task_templates");

            migrationBuilder.DropTable(
                name: "workflow_state");
        }
    }
}
