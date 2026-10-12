using Backend.Shared.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WCSBackend.Migrations;

[DbContext(typeof(GrcsDbContext))]
[Migration("20261010150000_RemoveTaskTemplateDescriptionCategory")]
public partial class RemoveTaskTemplateDescriptionCategory : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "description", table: "task_templates");
        migrationBuilder.DropColumn(name: "category", table: "task_templates");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "description",
            table: "task_templates",
            type: "longtext",
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "category",
            table: "task_templates",
            type: "longtext",
            nullable: true);

        migrationBuilder.Sql("UPDATE `task_templates` SET `description` = '' WHERE `description` IS NULL;");
        migrationBuilder.Sql("UPDATE `task_templates` SET `category` = '' WHERE `category` IS NULL;");

        migrationBuilder.AlterColumn<string>(
            name: "description",
            table: "task_templates",
            type: "longtext",
            nullable: false,
            oldClrType: typeof(string),
            oldType: "longtext",
            oldNullable: true);
        migrationBuilder.AlterColumn<string>(
            name: "category",
            table: "task_templates",
            type: "longtext",
            nullable: false,
            oldClrType: typeof(string),
            oldType: "longtext",
            oldNullable: true);
    }

    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
#pragma warning disable 612, 618
        modelBuilder.HasAnnotation("ProductVersion", "9.0.16")
            .HasAnnotation("Relational:MaxIdentifierLength", 64);
        MySqlModelBuilderExtensions.AutoIncrementColumns(modelBuilder);
        modelBuilder.Entity("Contracts.Entities.TaskTemplateRow", b =>
        {
            b.Property<string>("Value").HasColumnType("varchar(255)").HasColumnName("value");
            b.Property<string>("ContainerMode").IsRequired().HasColumnType("longtext").HasColumnName("container_mode");
            b.Property<string>("ContainerPrefix").IsRequired().HasColumnType("longtext").HasColumnName("container_prefix");
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
