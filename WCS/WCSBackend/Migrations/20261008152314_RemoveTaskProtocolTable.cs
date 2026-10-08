using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WCSBackend.Migrations
{
    /// <inheritdoc />
    public partial class RemoveTaskProtocolTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 保留尚未并入业务模板表的旧任务协议配置，再删除独立表。
            migrationBuilder.Sql(@"
INSERT INTO `feature_modules`
    (`rcs_type`, `module_id`, `template_kind`, `profile_name`, `base_url`, `is_active`, `name`, `api_url`,
     `http_method`, `request_body_template`, `success_property`, `success_value`, `timeout_seconds`,
     `pre_execution_effect`, `inventory_effect`, `params_json`, `updated_at`)
SELECT p.`rcs_type`, p.`operation_key`, 'task_protocol', p.`profile_name`, p.`base_url`, p.`is_active`, p.`name`, p.`api_path`,
       p.`http_method`, p.`request_body_template`, p.`success_property`, p.`success_value`, p.`timeout_seconds`,
       '', '', '[]', p.`updated_at`
FROM `rcs_task_protocol_templates` p
WHERE NOT EXISTS (
    SELECT 1 FROM `feature_modules` f
    WHERE f.`rcs_type` = p.`rcs_type` AND f.`module_id` = p.`operation_key`
);");

            migrationBuilder.DropTable(
                name: "rcs_task_protocol_templates");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "rcs_task_protocol_templates",
                columns: table => new
                {
                    rcs_type = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    operation_key = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    api_path = table.Column<string>(type: "varchar(2048)", maxLength: 2048, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    base_url = table.Column<string>(type: "varchar(2048)", maxLength: 2048, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    http_method = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    is_active = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    name = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    profile_name = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false)
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

            migrationBuilder.Sql(@"
INSERT INTO `rcs_task_protocol_templates`
    (`rcs_type`, `operation_key`, `profile_name`, `base_url`, `is_active`, `name`, `http_method`, `api_path`,
     `request_body_template`, `success_property`, `success_value`, `timeout_seconds`, `updated_at`)
SELECT f.`rcs_type`, f.`module_id`, f.`profile_name`, f.`base_url`, f.`is_active`, f.`name`, f.`http_method`, f.`api_url`,
       f.`request_body_template`, f.`success_property`, f.`success_value`, f.`timeout_seconds`, f.`updated_at`
FROM `feature_modules` f
WHERE f.`template_kind` = 'task_protocol'
  AND NOT EXISTS (
      SELECT 1 FROM `rcs_task_protocol_templates` p
      WHERE p.`rcs_type` = f.`rcs_type` AND p.`operation_key` = f.`module_id`
  );

DELETE FROM `feature_modules` WHERE `template_kind` = 'task_protocol';");
        }
    }
}
