using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WCSBackend.Migrations;

/// <summary>Replaces the ambiguous task-record ok flag with an explicit stage status.</summary>
public partial class ReplaceTaskRecordOkWithStageStatus : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE "__task_records_status" (
                "id" INTEGER NOT NULL CONSTRAINT "PK_task_records" PRIMARY KEY AUTOINCREMENT,
                "task_id" TEXT NOT NULL,
                "stage" TEXT NOT NULL,
                "time" TEXT NOT NULL,
                "warehouse" TEXT NOT NULL,
                "container_code" TEXT NOT NULL,
                "cargo_code" TEXT NOT NULL,
                "task_type" TEXT NOT NULL,
                "start_station_code" TEXT NOT NULL,
                "end_station_code" TEXT NOT NULL,
                "stage_status" TEXT NOT NULL,
                "status_code" INTEGER NOT NULL
            );
            """);
        migrationBuilder.Sql("""
            INSERT INTO "__task_records_status" (
                "id", "task_id", "stage", "time", "warehouse", "container_code", "cargo_code", "task_type",
                "start_station_code", "end_station_code", "stage_status", "status_code")
            SELECT
                "id", "task_id", "stage", "time", "warehouse", "container_code", "cargo_code", "task_type",
                "start_station_code", "end_station_code",
                CASE
                    WHEN "stage" IN ('START', 'LOAD_FINISH', 'FINISHED') THEN 'callback'
                    WHEN "ok" = 1 THEN 'success'
                    ELSE 'fail'
                END,
                "status_code"
            FROM "task_records";
            """);
        migrationBuilder.Sql("""DROP TABLE "task_records";""");
        migrationBuilder.Sql("""ALTER TABLE "__task_records_status" RENAME TO "task_records";""");
        migrationBuilder.Sql("""CREATE INDEX "idx_tr_task" ON "task_records" ("task_id");""");
        migrationBuilder.Sql("""CREATE INDEX "idx_tr_stage" ON "task_records" ("stage");""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE "__task_records_ok" (
                "id" INTEGER NOT NULL CONSTRAINT "PK_task_records" PRIMARY KEY AUTOINCREMENT,
                "task_id" TEXT NOT NULL,
                "stage" TEXT NOT NULL,
                "time" TEXT NOT NULL,
                "warehouse" TEXT NOT NULL,
                "container_code" TEXT NOT NULL,
                "cargo_code" TEXT NOT NULL,
                "task_type" TEXT NOT NULL,
                "start_station_code" TEXT NOT NULL,
                "end_station_code" TEXT NOT NULL,
                "ok" INTEGER NOT NULL,
                "status_code" INTEGER NOT NULL
            );
            """);
        migrationBuilder.Sql("""
            INSERT INTO "__task_records_ok" (
                "id", "task_id", "stage", "time", "warehouse", "container_code", "cargo_code", "task_type",
                "start_station_code", "end_station_code", "ok", "status_code")
            SELECT
                "id", "task_id", "stage", "time", "warehouse", "container_code", "cargo_code", "task_type",
                "start_station_code", "end_station_code",
                CASE WHEN "stage_status" = 'success' THEN 1 ELSE 0 END,
                "status_code"
            FROM "task_records";
            """);
        migrationBuilder.Sql("""DROP TABLE "task_records";""");
        migrationBuilder.Sql("""ALTER TABLE "__task_records_ok" RENAME TO "task_records";""");
        migrationBuilder.Sql("""CREATE INDEX "idx_tr_task" ON "task_records" ("task_id");""");
        migrationBuilder.Sql("""CREATE INDEX "idx_tr_stage" ON "task_records" ("stage");""");
    }
}
