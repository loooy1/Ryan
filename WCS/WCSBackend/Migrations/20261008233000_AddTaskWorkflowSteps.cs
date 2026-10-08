using Backend.Shared.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WCSBackend.Migrations;

[DbContext(typeof(GrcsDbContext))]
[Migration("20261008233000_AddTaskWorkflowSteps")]
public partial class AddTaskWorkflowSteps : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "workflow_json",
            table: "task_templates",
            type: "longtext",
            nullable: true);

        migrationBuilder.Sql("UPDATE `task_templates` SET `workflow_json` = '[]' WHERE `workflow_json` IS NULL;");

        migrationBuilder.AlterColumn<string>(
            name: "workflow_json",
            table: "task_templates",
            type: "longtext",
            nullable: false,
            oldClrType: typeof(string),
            oldType: "longtext",
            oldNullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.DropColumn(name: "workflow_json", table: "task_templates");

    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
#pragma warning disable 612, 618
        modelBuilder.HasAnnotation("ProductVersion", "9.0.16")
            .HasAnnotation("Relational:MaxIdentifierLength", 64);
        MySqlModelBuilderExtensions.AutoIncrementColumns(modelBuilder);
        // This is a snapshot of the provider model (converted JSON properties are strings),
        // so use the entity name instead of the CLR type to avoid conflicting with the
        // TaskPointDto/List<TaskWorkflowStepDto> CLR property types.
        modelBuilder.Entity("Contracts.Entities.TaskTemplateRow", b =>
        {
            b.Property<string>("Value").HasColumnType("varchar(255)").HasColumnName("value");
            b.Property<string>("Category").IsRequired().HasColumnType("longtext").HasColumnName("category");
            b.Property<string>("ContainerMode").IsRequired().HasColumnType("longtext").HasColumnName("container_mode");
            b.Property<string>("ContainerPrefix").IsRequired().HasColumnType("longtext").HasColumnName("container_prefix");
            b.Property<string>("Description").IsRequired().HasColumnType("longtext").HasColumnName("description");
            b.Property<string>("End").IsRequired().HasColumnType("longtext").HasColumnName("end_json");
            b.Property<string>("Label").IsRequired().HasColumnType("longtext").HasColumnName("label");
            b.Property<bool>("NeedsContainer").HasColumnType("tinyint(1)").HasColumnName("needs_container");
            b.Property<bool>("RandomContainer").HasColumnType("tinyint(1)").HasColumnName("random_container");
            b.Property<string>("Start").IsRequired().HasColumnType("longtext").HasColumnName("start_json");
            b.Property<string>("UpdatedAt").IsRequired().HasColumnType("longtext").HasColumnName("updated_at");
            b.Property<string>("WorkflowSteps").IsRequired().HasColumnType("longtext").HasColumnName("workflow_json");
            b.HasKey("Value");
            b.ToTable("task_templates", (string)null);
        });
#pragma warning restore 612, 618
    }
}
