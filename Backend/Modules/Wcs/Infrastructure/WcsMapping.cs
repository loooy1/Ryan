using System.Globalization;
using System.Text.Json;
using Mapster;
using Contracts.Dtos;
using Contracts.Entities;

namespace WCSBackend.Modules.Wcs.Infrastructure;

/// <summary>
/// DTO ↔ 实体全量映射注册（Mapster 全局配置，启动时 Build 一次）。
/// 覆盖：模板 4 表、Mock 审批事件、台账条目 ↔ 任务记录、kv 配置类（JSON 值列）。
/// 聚合/推导逻辑（事件流、分组、选点）不在映射层，保留在 Service 内手写。
/// </summary>
public static class WcsMapping
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public static void Build()
    {
        // ── 模板 4 表：DTO ↔ Row（JSON 列经 EF 值转换器序列化，此处直接同名字段映射）──
        TypeAdapterConfig.GlobalSettings.NewConfig<TaskTemplateDto, TaskTemplateRow>()
            .AfterMapping((_, d) => d.UpdatedAt = Now());
        TypeAdapterConfig.GlobalSettings.NewConfig<TaskTemplateRow, TaskTemplateDto>();

        TypeAdapterConfig.GlobalSettings.NewConfig<FeatureModuleDto, FeatureModuleRow>()
            .AfterMapping((_, d) => d.UpdatedAt = Now());
        TypeAdapterConfig.GlobalSettings.NewConfig<FeatureModuleRow, FeatureModuleDto>();

        TypeAdapterConfig.GlobalSettings.NewConfig<AutoTemplateDto, AutoTemplateRow>()
            .AfterMapping((_, d) => d.UpdatedAt = Now());
        TypeAdapterConfig.GlobalSettings.NewConfig<AutoTemplateRow, AutoTemplateDto>();

        TypeAdapterConfig.GlobalSettings.NewConfig<MockRuleDto, MockRuleRow>()
            .AfterMapping((_, d) => d.UpdatedAt = Now());
        TypeAdapterConfig.GlobalSettings.NewConfig<MockRuleRow, MockRuleDto>();

        // ── Mock 审批事件：ISO 字符串列 ↔ DateTime ──
        TypeAdapterConfig.GlobalSettings.NewConfig<MockRequestEventDto, MockRequestEventRow>()
            .Map(d => d.Time, s => s.Time.ToString("O"))
            .Map(d => d.DecidedAt, s => s.DecidedAt.HasValue ? s.DecidedAt.Value.ToString("O") : null);
        TypeAdapterConfig.GlobalSettings.NewConfig<MockRequestEventRow, MockRequestEventDto>()
            .Map(d => d.Time, s => ParseTime(s.Time))
            .Map(d => d.DecidedAt, s => s.DecidedAt == null ? (DateTime?)null : ParseTime(s.DecidedAt));

        // ── 台账条目 ↔ 任务记录（创建行语义：stage=CREATED、RouteCodes=站点对、StationCode 留空）──
        TypeAdapterConfig.GlobalSettings.NewConfig<TaskLedgerEntry, TaskRecord>()
            .Map(d => d.Stage, _ => "CREATED")
            .Map(d => d.Time, s => ParseLedgerTime(s.Time))
            .Map(d => d.RouteCodes, s => s.StationCode)
            .Map(d => d.StationCode, _ => "")
            .AfterMapping((_, d) => d.Id = 0);
        TypeAdapterConfig.GlobalSettings.NewConfig<TaskRecord, TaskLedgerEntry>()
            .Map(d => d.StationCode, s => s.RouteCodes)
            .Map(d => d.Time, s => s.Time.ToString("O"));

        // ── 任务记录 → 阶段事件（时间线/分拣卡片）──
        TypeAdapterConfig.GlobalSettings.NewConfig<TaskRecord, StageChangeEvent>()
            .Map(d => d.Id, s => s.Id)
            .Map(d => d.TaskId, s => s.TaskId)
            .Map(d => d.Warehouse, s => s.Warehouse)
            .Map(d => d.StationCode, s => s.StationCode)
            .Map(d => d.ContainerCode, s => s.ContainerCode)
            .Map(d => d.Stage, s => s.Stage)
            .Map(d => d.Time, s => s.Time);

        // ── kv 配置类：对象 ↔ KvRow.Value（JSON 值列）──
        KvMap<WcsSettingsDto>("wcs_settings");
        KvMap<RangeConfigDto>("auto_range");
        KvMap<NestConfigDto>("nest_config");
        KvMap<MapUploadDto>("map_stations");
    }

    private static void KvMap<T>(string key) where T : class, new()
    {
        TypeAdapterConfig.GlobalSettings.NewConfig<T, KvRow>()
            .Map(d => d.Key, _ => key)
            .Map(d => d.Value, s => JsonSerializer.Serialize(s, JsonOpts));
        TypeAdapterConfig.GlobalSettings.NewConfig<KvRow, T>()
            .MapWith(s => string.IsNullOrEmpty(s.Value) ? new T() : JsonSerializer.Deserialize<T>(s.Value, JsonOpts) ?? new T());
    }

    private static string Now() => DateTime.Now.ToString("O");

    private static DateTime ParseTime(string s) => DateTime.TryParse(s, out var t) ? t : DateTime.Now;

    private static DateTime ParseLedgerTime(string s)
        => DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) ? t : DateTime.Now;
}