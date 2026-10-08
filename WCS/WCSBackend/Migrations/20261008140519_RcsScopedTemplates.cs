using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WCSBackend.Migrations
{
    /// <inheritdoc />
    public partial class RcsScopedTemplates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_mock_rules",
                table: "mock_rules");

            migrationBuilder.DropPrimaryKey(
                name: "PK_feature_modules",
                table: "feature_modules");

            migrationBuilder.AddColumn<string>(
                name: "rcs_type",
                table: "mock_rules",
                type: "varchar(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "RCS1")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "rcs_type",
                table: "feature_modules",
                type: "varchar(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "RCS1")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddPrimaryKey(
                name: "PK_mock_rules",
                table: "mock_rules",
                columns: new[] { "rcs_type", "rule_id" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_feature_modules",
                table: "feature_modules",
                columns: new[] { "rcs_type", "module_id" });

            migrationBuilder.CreateTable(
                name: "rcs_task_protocol_templates",
                columns: table => new
                {
                    rcs_type = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    operation_key = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    profile_name = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    base_url = table.Column<string>(type: "varchar(2048)", maxLength: 2048, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    is_active = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    name = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    http_method = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    api_path = table.Column<string>(type: "varchar(2048)", maxLength: 2048, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    request_body_template = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    success_property = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    success_value = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    timeout_seconds = table.Column<int>(type: "int", nullable: false),
                    updated_at = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rcs_task_protocol_templates", x => new { x.rcs_type, x.operation_key });
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "idx_rcs_task_protocol_active",
                table: "rcs_task_protocol_templates",
                column: "is_active");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "rcs_task_protocol_templates");

            migrationBuilder.DropPrimaryKey(
                name: "PK_mock_rules",
                table: "mock_rules");

            migrationBuilder.DropPrimaryKey(
                name: "PK_feature_modules",
                table: "feature_modules");

            migrationBuilder.DropColumn(
                name: "rcs_type",
                table: "mock_rules");

            migrationBuilder.DropColumn(
                name: "rcs_type",
                table: "feature_modules");

            migrationBuilder.AddPrimaryKey(
                name: "PK_mock_rules",
                table: "mock_rules",
                column: "rule_id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_feature_modules",
                table: "feature_modules",
                column: "module_id");
        }
    }
}
