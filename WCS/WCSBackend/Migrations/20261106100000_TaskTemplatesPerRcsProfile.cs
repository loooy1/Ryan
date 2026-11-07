using Backend.Shared.Infrastructure;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace WCSBackend.Migrations;

[DbContext(typeof(GrcsDbContext))]
[Migration("20261106100000_TaskTemplatesPerRcsProfile")]
public sealed class TaskTemplatesPerRcsProfile : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "rcs_type",
            table: "task_templates",
            type: "varchar(64)",
            maxLength: 64,
            nullable: false,
            defaultValue: "RCS1");

        // Associate legacy global templates with the currently active RCS profile.
        migrationBuilder.Sql("""
            UPDATE task_templates
            SET rcs_type = COALESCE(
                (SELECT active_profile.rcs_type
                 FROM (SELECT rcs_type FROM feature_modules WHERE is_active = 1 ORDER BY rcs_type LIMIT 1) AS active_profile),
                'RCS1');
            """);

        migrationBuilder.Sql("ALTER TABLE task_templates DROP PRIMARY KEY, ADD PRIMARY KEY (rcs_type, value);");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Older schema cannot represent duplicate template values across profiles.
        migrationBuilder.Sql("DELETE duplicate_row FROM task_templates duplicate_row JOIN task_templates kept_row ON duplicate_row.value = kept_row.value AND duplicate_row.rcs_type > kept_row.rcs_type;");
        migrationBuilder.Sql("ALTER TABLE task_templates DROP PRIMARY KEY, ADD PRIMARY KEY (value);");
        migrationBuilder.DropColumn(name: "rcs_type", table: "task_templates");
    }
}
