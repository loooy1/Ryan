using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WCSBackend.Migrations
{
    /// <inheritdoc />
    public partial class TaskDispatchTemplatesAsBusinessFeatures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "base_url",
                table: "feature_modules",
                type: "varchar(2048)",
                maxLength: 2048,
                nullable: false,
                defaultValue: "")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "http_method",
                table: "feature_modules",
                type: "varchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "POST")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<bool>(
                name: "is_active",
                table: "feature_modules",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "profile_name",
                table: "feature_modules",
                type: "varchar(255)",
                maxLength: 255,
                nullable: false,
                defaultValue: "")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "request_body_template",
                table: "feature_modules",
                type: "longtext",
                nullable: false,
                defaultValue: "")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "success_property",
                table: "feature_modules",
                type: "longtext",
                nullable: false,
                defaultValue: "")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "success_value",
                table: "feature_modules",
                type: "varchar(255)",
                maxLength: 255,
                nullable: false,
                defaultValue: "true")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "template_kind",
                table: "feature_modules",
                type: "varchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "module")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<int>(
                name: "timeout_seconds",
                table: "feature_modules",
                type: "int",
                nullable: false,
                defaultValue: 10);

            // 搬运/移动下发是业务功能模板的一部分：迁入 feature_modules 并以 template_kind 区分。
            migrationBuilder.Sql(@"
INSERT INTO `feature_modules`
    (`rcs_type`, `module_id`, `template_kind`, `profile_name`, `base_url`, `is_active`, `name`, `api_url`,
     `http_method`, `request_body_template`, `success_property`, `success_value`, `timeout_seconds`,
     `pre_execution_effect`, `inventory_effect`, `params_json`, `updated_at`)
SELECT p.`rcs_type`, p.`operation_key`, 'task_protocol', p.`profile_name`, p.`base_url`, p.`is_active`, p.`name`, p.`api_path`,
       p.`http_method`, p.`request_body_template`, p.`success_property`, p.`success_value`, p.`timeout_seconds`,
       '', '', '[]', p.`updated_at`
FROM `rcs_task_protocol_templates` p
WHERE p.`operation_key` IN ('task.carry', 'task.move')
  AND NOT EXISTS (
      SELECT 1 FROM `feature_modules` f
      WHERE f.`rcs_type` = p.`rcs_type` AND f.`module_id` = p.`operation_key`
  );

DELETE FROM `rcs_task_protocol_templates`
WHERE `operation_key` IN ('task.carry', 'task.move');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
INSERT INTO `rcs_task_protocol_templates`
    (`rcs_type`, `operation_key`, `profile_name`, `base_url`, `is_active`, `name`, `http_method`, `api_path`,
     `request_body_template`, `success_property`, `success_value`, `timeout_seconds`, `updated_at`)
SELECT f.`rcs_type`, f.`module_id`, f.`profile_name`, f.`base_url`, f.`is_active`, f.`name`, f.`http_method`, f.`api_url`,
       f.`request_body_template`, f.`success_property`, f.`success_value`, f.`timeout_seconds`, f.`updated_at`
FROM `feature_modules` f
WHERE f.`template_kind` = 'task_protocol'
  AND f.`module_id` IN ('task.carry', 'task.move')
  AND NOT EXISTS (
      SELECT 1 FROM `rcs_task_protocol_templates` p
      WHERE p.`rcs_type` = f.`rcs_type` AND p.`operation_key` = f.`module_id`
  );

DELETE FROM `feature_modules`
WHERE `template_kind` = 'task_protocol'
  AND `module_id` IN ('task.carry', 'task.move');");

            migrationBuilder.DropColumn(
                name: "base_url",
                table: "feature_modules");

            migrationBuilder.DropColumn(
                name: "http_method",
                table: "feature_modules");

            migrationBuilder.DropColumn(
                name: "is_active",
                table: "feature_modules");

            migrationBuilder.DropColumn(
                name: "profile_name",
                table: "feature_modules");

            migrationBuilder.DropColumn(
                name: "request_body_template",
                table: "feature_modules");

            migrationBuilder.DropColumn(
                name: "success_property",
                table: "feature_modules");

            migrationBuilder.DropColumn(
                name: "success_value",
                table: "feature_modules");

            migrationBuilder.DropColumn(
                name: "template_kind",
                table: "feature_modules");

            migrationBuilder.DropColumn(
                name: "timeout_seconds",
                table: "feature_modules");
        }
    }
}
