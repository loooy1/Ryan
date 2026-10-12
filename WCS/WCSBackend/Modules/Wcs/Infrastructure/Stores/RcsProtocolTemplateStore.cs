using System.Text.Json;
using Backend.Shared.Infrastructure.Repository;
using Contracts.Dtos;
using Contracts.Entities;
using Mapster;
using WCSBackend.Modules.Wcs.Infrastructure.Persistence.Entities;
using WCSBackend.Modules.Wcs.Models;

namespace WCSBackend.Modules.Wcs.Infrastructure;

/// <summary>WCS RCS 模板存储：Mock 和业务功能模板按 RCS 类型分别保存；任务下发模板也是业务功能模板的一种。</summary>
public sealed class RcsProtocolTemplateStore
{
    private const string LegacyStorageKey = "rcs_protocol_profiles";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly IUnitOfWorkFactory _uow;
    private readonly WcsSettingsService _settings;
    private readonly object _sync = new();
    private RcsProtocolProfilesDto _config;

    public RcsProtocolTemplateStore(IUnitOfWorkFactory uow, WcsSettingsService settings)
    {
        _uow = uow;
        _settings = settings;
        _config = LoadOrSeed();
    }

    public RcsProtocolProfilesDto GetAll()
    {
        lock (_sync) return Clone(_config);
    }

    public RcsProtocolProfileDto GetActive()
    {
        lock (_sync)
        {
            var profile = _config.Profiles.FirstOrDefault(x => Eq(x.Id, _config.ActiveProfileId))
                ?? _config.Profiles.FirstOrDefault();
            return profile == null ? CreateDefaultProfile() : Clone(profile);
        }
    }

    public string ResolveBaseUrl(string? fallback)
    {
        var profileUrl = GetActive().BaseUrl?.Trim();
        return !string.IsNullOrWhiteSpace(profileUrl) ? profileUrl.TrimEnd('/') : (fallback ?? "").TrimEnd('/');
    }

    public RcsProtocolProfilesDto Save(RcsProtocolProfilesDto config)
    {
        if (config == null) throw new ArgumentNullException(nameof(config));
        var profiles = Clone(config).Profiles ?? [];
        if (profiles.Count == 0) throw new InvalidOperationException("至少需要保留一个 RCS 配置集。");
        if (profiles.Any(p => string.IsNullOrWhiteSpace(p.Id) || string.IsNullOrWhiteSpace(p.Name)))
            throw new InvalidOperationException("RCS 配置集编码和名称不能为空。");
        if (profiles.Select(p => p.Id.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != profiles.Count)
            throw new InvalidOperationException("RCS 配置集编码不能重复。");

        foreach (var profile in profiles)
        {
            profile.Id = profile.Id.Trim();
            profile.Name = profile.Name.Trim();
            profile.BaseUrl = profile.BaseUrl?.Trim() ?? "";
            if (!string.IsNullOrWhiteSpace(profile.BaseUrl)
                && (!Uri.TryCreate(profile.BaseUrl, UriKind.Absolute, out var profileUri)
                    || profileUri.Scheme is not ("http" or "https")))
                throw new InvalidOperationException($"RCS 配置集「{profile.Name}」的后端地址必须是完整 HTTP/HTTPS 地址。");

            profile.Operations ??= [];
            profile.FeatureModules ??= [];
            profile.MockRules ??= [];
            NormalizeTaskInterfaceAssignments(profile.FeatureModules);
            profile.Operations = BuildOperations(profile.FeatureModules);
            EnsureRequiredOperations(profile);
            foreach (var op in profile.Operations)
            {
                op.Key = op.Key?.Trim() ?? "";
                op.Name = op.Name?.Trim() ?? "";
                op.HttpMethod = NormalizeMethod(op.HttpMethod);
                op.ApiPath = op.ApiPath?.Trim() ?? "";
                op.RequestBodyTemplate ??= "";
                op.SuccessProperty ??= "";
                op.SuccessValue = string.IsNullOrWhiteSpace(op.SuccessValue) ? "true" : op.SuccessValue.Trim();
                op.TimeoutSeconds = Math.Clamp(op.TimeoutSeconds, 1, 120);
                if (string.IsNullOrWhiteSpace(op.Key) || string.IsNullOrWhiteSpace(op.ApiPath))
                    throw new InvalidOperationException($"配置集「{profile.Name}」中的协议操作名称、编码和地址不能为空。");
            }
            if (profile.Operations.Select(o => o.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count() != profile.Operations.Count)
                throw new InvalidOperationException($"配置集「{profile.Name}」中存在重复的操作编码。");
            foreach (var requiredKey in new[] { "task.carry", "task.move" })
                if (!profile.Operations.Any(o => Eq(o.Key, requiredKey)))
                    throw new InvalidOperationException($"配置集「{profile.Name}」必须保留 {requiredKey} 模板。");
            EnforceSingleBoardSync(profile.MockRules);
        }

        var activeProfileId = config.ActiveProfileId?.Trim() ?? "";
        if (!profiles.Any(p => Eq(p.Id, activeProfileId)))
            throw new InvalidOperationException("当前启用的 RCS 配置集不存在。");

        lock (_sync)
        {
            _config = new RcsProtocolProfilesDto { SchemaVersion = 3, ActiveProfileId = activeProfileId, Profiles = profiles };
            Persist();
            SyncActiveBaseUrl();
            return Clone(_config);
        }
    }

    public RcsProtocolProfilesDto Activate(string profileId)
    {
        lock (_sync)
        {
            if (!_config.Profiles.Any(p => Eq(p.Id, profileId)))
                throw new InvalidOperationException($"RCS 配置集「{profileId}」不存在。");
            _config.ActiveProfileId = profileId.Trim();
            Persist();
            SyncActiveBaseUrl();
            return Clone(_config);
        }
    }

    public RcsProtocolProfilesDto Delete(string profileId)
    {
        lock (_sync)
        {
            var profile = _config.Profiles.FirstOrDefault(p => Eq(p.Id, profileId));
            if (profile == null) throw new InvalidOperationException($"RCS 模板「{profileId}」不存在。");
            if (_config.Profiles.Count <= 1) throw new InvalidOperationException("至少保留一个 RCS 模板，不能删除最后一套。");

            var wasActive = Eq(_config.ActiveProfileId, profile.Id);
            _config.Profiles.Remove(profile);
            if (wasActive) _config.ActiveProfileId = _config.Profiles[0].Id;
            Persist(); // 两张表整体重写，删除类型时同时清除其 Mock 和业务模板行。
            if (wasActive) SyncActiveBaseUrl();
            return Clone(_config);
        }
    }

    public RcsProtocolOperationTemplateDto GetRequiredOperation(string key)
    {
        var operation = GetActive().Operations.FirstOrDefault(x => Eq(x.Key, key));
        return operation ?? throw new InvalidOperationException($"当前 RCS 配置集缺少协议模板：{key}");
    }

    public bool HasTaskInterface(string taskInterfaceType)
        => GetActive().FeatureModules.Any(module =>
            Eq(module.TaskInterfaceType, taskInterfaceType) && !string.IsNullOrWhiteSpace(module.ApiUrl));

    public void ReplaceActiveFeatureModules(IEnumerable<FeatureModuleDto> modules)
    {
        lock (_sync)
        {
            var profile = GetActiveMutable();
            profile.FeatureModules = modules?.ToList() ?? [];
            NormalizeTaskInterfaceAssignments(profile.FeatureModules);
            profile.Operations = BuildOperations(profile.FeatureModules);
            EnsureRequiredOperations(profile);
            Persist();
        }
    }

    public void ReplaceActiveMockRules(IEnumerable<MockRuleDto> rules)
    {
        lock (_sync)
        {
            var profile = GetActiveMutable();
            profile.MockRules = rules?.ToList() ?? [];
            EnforceSingleBoardSync(profile.MockRules);
            Persist();
        }
    }

    public bool RemoveActiveMockRule(string id)
    {
        lock (_sync)
        {
            var profile = _config.Profiles.FirstOrDefault(p => Eq(p.Id, _config.ActiveProfileId));
            if (profile == null) return false;
            var removed = profile.MockRules.RemoveAll(x => Eq(x.Id, id)) > 0;
            if (removed) Persist();
            return removed;
        }
    }

    public void UpdateActiveBaseUrl(string baseUrl)
    {
        lock (_sync)
        {
            var profile = GetActiveMutable();
            profile.BaseUrl = baseUrl?.Trim() ?? "";
            Persist();
        }
    }

    private RcsProtocolProfilesDto LoadOrSeed()
    {
        using var uow = _uow.Create();
        var featureRows = uow.Repository<RcsFeatureModuleRow>().FindAllAsync().GetAwaiter().GetResult();
        var mockRows = uow.Repository<RcsMockRuleRow>().FindAllAsync().GetAwaiter().GetResult();

        // Import the complete legacy KV document once, then remove it so templates have one source of truth.
        var legacyJson = uow.Repository<KvRow>().FindAsync(LegacyStorageKey).GetAwaiter().GetResult()?.Value;
        if (!string.IsNullOrWhiteSpace(legacyJson))
        {
            try
            {
                var legacy = JsonSerializer.Deserialize<RcsProtocolProfilesDto>(legacyJson, JsonOptions);
                if (legacy?.Profiles is { Count: > 0 })
                {
                    NormalizeProfiles(legacy.Profiles);
                    legacy.SchemaVersion = 3;
                    if (!legacy.Profiles.Any(x => Eq(x.Id, legacy.ActiveProfileId))) legacy.ActiveProfileId = legacy.Profiles[0].Id;
                    PersistToDatabase(legacy);
                    uow.Repository<KvRow>().DeleteWhereAsync(x => x.Key == LegacyStorageKey).GetAwaiter().GetResult();
                    uow.CommitAsync().GetAwaiter().GetResult();
                    return legacy;
                }
            }
            catch (JsonException) { }
        }

        var types = featureRows.Select(x => x.RcsType)
            .Concat(mockRows.Select(x => x.RcsType))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (types.Count == 0) types.Add("RCS1");

        var requiresWrite = false;
        var profiles = types.Select(type =>
        {
            var profileRows = featureRows.Where(x => Eq(x.RcsType, type)).ToList();
            var profileFeature = profileRows.FirstOrDefault();
            var featureTemplates = profileRows.Select(x => x.Adapt<FeatureModuleDto>()).ToList();
            var mockTemplates = mockRows.Where(x => Eq(x.RcsType, type)).Select(x => x.Adapt<MockRuleDto>()).ToList();
            var profile = new RcsProtocolProfileDto
            {
                Id = type,
                Name = !string.IsNullOrWhiteSpace(profileFeature?.ProfileName) ? profileFeature!.ProfileName : type,
                BaseUrl = profileFeature?.BaseUrl ?? (Eq(type, "RCS1") ? _settings.Get().GrcsBaseUrl : ""),
                Operations = BuildOperations(featureTemplates),
                FeatureModules = featureTemplates,
                MockRules = mockTemplates,
            };
            EnsureRequiredOperations(profile);
            profile.MockRules = EnsureDefaultMockRule(profile.MockRules);
            if (profile.Operations.Count != profileRows.Count(x => IsProtocolOperationKey(x.Id)) || profile.MockRules.Count != mockTemplates.Count)
                requiresWrite = true;
            return profile;
        }).ToList();

        var active = featureRows.FirstOrDefault(x => x.IsActive)?.RcsType;
        var seeded = new RcsProtocolProfilesDto
        {
            SchemaVersion = 3,
            ActiveProfileId = profiles.FirstOrDefault(x => Eq(x.Id, active))?.Id ?? profiles[0].Id,
            Profiles = profiles,
        };
        if (requiresWrite) PersistToDatabase(seeded);
        if (!string.IsNullOrWhiteSpace(legacyJson))
        {
            uow.Repository<KvRow>().DeleteWhereAsync(x => x.Key == LegacyStorageKey).GetAwaiter().GetResult();
            uow.CommitAsync().GetAwaiter().GetResult();
        }
        return seeded;
    }

    private void Persist()
    {
        _config.SchemaVersion = 3;
        PersistToDatabase(_config);
    }

    private void PersistToDatabase(RcsProtocolProfilesDto config)
    {
        using var uow = _uow.Create();
        uow.Repository<RcsMockRuleRow>().DeleteWhereAsync(_ => true).GetAwaiter().GetResult();
        uow.Repository<RcsFeatureModuleRow>().DeleteWhereAsync(_ => true).GetAwaiter().GetResult();

        foreach (var profile in config.Profiles)
        {
            foreach (var dto in profile.MockRules ?? [])
            {
                var row = dto.Adapt<RcsMockRuleRow>();
                row.RcsType = profile.Id;
                uow.Repository<RcsMockRuleRow>().AddAsync(row).GetAwaiter().GetResult();
            }
            var templates = (profile.FeatureModules ?? [])
                .Where(x => !string.IsNullOrWhiteSpace(x.Id))
                .ToDictionary(x => x.Id, x => x, StringComparer.OrdinalIgnoreCase);
            foreach (var op in profile.Operations ?? [])
            {
                var dto = templates.Values.FirstOrDefault(x => Eq(ResolveTaskOperationKey(x.TaskInterfaceType), op.Key));
                if (dto == null) templates.TryGetValue(op.Key, out dto);
                if (dto == null)
                {
                    dto = new FeatureModuleDto { Id = op.Key };
                    templates.Add(op.Key, dto);
                }
                dto.Name = op.Name;
                dto.ApiUrl = op.ApiPath;
                dto.HttpMethod = op.HttpMethod;
                dto.RequestBodyTemplate = op.RequestBodyTemplate;
                dto.SuccessProperty = op.SuccessProperty;
                dto.SuccessValue = op.SuccessValue;
                dto.TimeoutSeconds = op.TimeoutSeconds;
            }
            // Keep one in-memory collection as well as one database row per template.
            profile.FeatureModules = templates.Values.ToList();
            foreach (var dto in templates.Values)
            {
                var row = dto.Adapt<RcsFeatureModuleRow>();
                row.RcsType = profile.Id;
                row.ProfileName = profile.Name;
                row.BaseUrl = profile.BaseUrl;
                row.IsActive = Eq(profile.Id, config.ActiveProfileId);
                row.UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                uow.Repository<RcsFeatureModuleRow>().AddAsync(row).GetAwaiter().GetResult();
            }
        }
        uow.CommitAsync().GetAwaiter().GetResult();
    }

    private static void NormalizeProfiles(List<RcsProtocolProfileDto> profiles)
    {
        foreach (var profile in profiles)
        {
            profile.Id = profile.Id?.Trim() ?? "";
            profile.Name = string.IsNullOrWhiteSpace(profile.Name) ? profile.Id : profile.Name.Trim();
            profile.BaseUrl ??= "";
            profile.Operations ??= [];
            profile.FeatureModules ??= [];
            profile.MockRules ??= [];
            EnsureRequiredOperations(profile);
            profile.MockRules = EnsureDefaultMockRule(profile.MockRules);
        }
    }

    private static void EnsureRequiredOperations(RcsProtocolProfileDto profile)
    {
        profile.Operations ??= [];
        var defaults = CreateDefaultOperations();
        foreach (var required in defaults)
            if (!profile.Operations.Any(x => Eq(x.Key, required.Key))) profile.Operations.Add(required);
    }

    private RcsProtocolProfileDto GetActiveMutable()
        => _config.Profiles.FirstOrDefault(p => Eq(p.Id, _config.ActiveProfileId))
            ?? throw new InvalidOperationException("当前 RCS 配置集不存在。");

    private static RcsProtocolProfileDto CreateDefaultProfile() => new()
    {
        Id = "RCS1", Name = "RCS1", Operations = CreateDefaultOperations(),
    };

    private static List<RcsProtocolOperationTemplateDto> CreateDefaultOperations() =>
    [
        new() { Key = "task.carry", Name = "搬运任务下发", HttpMethod = "POST", ApiPath = "/api/v1/task_receive", SuccessProperty = "success", SuccessValue = "true", TimeoutSeconds = 10 },
        new() { Key = "task.move", Name = "移动任务下发", HttpMethod = "POST", ApiPath = "/api/RawOrder/ChangeFloor", SuccessProperty = "", SuccessValue = "true", TimeoutSeconds = 7 },
    ];

    private static List<RcsProtocolOperationTemplateDto> BuildOperations(IEnumerable<FeatureModuleDto> modules)
    {
        var items = modules.ToList();
        var result = new List<RcsProtocolOperationTemplateDto>();
        foreach (var key in new[] { "task.carry", "task.move" })
        {
            var role = Eq(key, "task.carry") ? TaskInterfaceTypes.Carry : TaskInterfaceTypes.Move;
            var module = items.FirstOrDefault(x => Eq(x.TaskInterfaceType, role))
                ?? items.FirstOrDefault(x => string.IsNullOrWhiteSpace(x.TaskInterfaceType) && Eq(x.Id, key));
            if (module != null) result.Add(ToOperation(module, key));
        }
        result.AddRange(items
            .Where(x => x.Id.StartsWith("custom.", StringComparison.OrdinalIgnoreCase))
            .Select(x => ToOperation(x, x.Id)));
        return result;
    }

    private static void NormalizeTaskInterfaceAssignments(List<FeatureModuleDto> modules)
    {
        foreach (var module in modules)
        {
            module.TaskInterfaceType = module.TaskInterfaceType?.Trim().ToUpperInvariant() switch
            {
                TaskInterfaceTypes.Carry => TaskInterfaceTypes.Carry,
                TaskInterfaceTypes.Move => TaskInterfaceTypes.Move,
                _ => TaskInterfaceTypes.None,
            };
        }

        foreach (var role in new[] { TaskInterfaceTypes.Carry, TaskInterfaceTypes.Move })
        {
            var matches = modules.Where(module => Eq(module.TaskInterfaceType, role)).ToList();
            foreach (var duplicate in matches.Skip(1)) duplicate.TaskInterfaceType = TaskInterfaceTypes.None;
        }
    }

    private static string? ResolveTaskOperationKey(string? taskInterfaceType) => taskInterfaceType?.Trim().ToUpperInvariant() switch
    {
        TaskInterfaceTypes.Carry => "task.carry",
        TaskInterfaceTypes.Move => "task.move",
        _ => null,
    };

    private static RcsProtocolOperationTemplateDto ToOperation(FeatureModuleDto module, string key) => new()
    {
        Key = key, Name = module.Name, HttpMethod = module.HttpMethod, ApiPath = module.ApiUrl,
        RequestBodyTemplate = module.RequestBodyTemplate, SuccessProperty = module.SuccessProperty,
        SuccessValue = module.SuccessValue, TimeoutSeconds = module.TimeoutSeconds,
    };

    private static bool IsProtocolOperationKey(string? key)
        => IsTaskDispatchOperation(key) || (key?.StartsWith("custom.", StringComparison.OrdinalIgnoreCase) ?? false);

    private static bool IsTaskDispatchOperation(string? key)
        => Eq(key, "task.carry") || Eq(key, "task.move");

    private static List<MockRuleDto> EnsureDefaultMockRule(List<MockRuleDto> items)
    {
        var result = items ?? [];
        var existing = result.FirstOrDefault(x => Eq(x.Id, "MOCK_DEFAULT"));
        if (existing != null && Eq(existing.PathPattern, "/api/v1/task_stage_change")) return result;
        if (existing == null && result.Any(x => x.Enabled && x.BoardSync)) return result;
        var rule = new MockRuleDto
        {
            Id = "MOCK_DEFAULT", Method = "POST", PathPattern = "/api/v1/task_stage_change", Matchers = [],
            ResponseCode = 200, ResponseBody = "{\"success\":true,\"message\":\"ok\"}", Enabled = true,
            Priority = 0, Description = "任务阶段模板卡：关联任务看板后，从请求中识别 taskId/stage 并推送看板。",
            AlsoRecord = true, BoardSync = true, RequiresApproval = false,
        };
        if (existing == null) result.Add(rule);
        else result[result.IndexOf(existing)] = rule;
        return result;
    }

    private static void EnforceSingleBoardSync(List<MockRuleDto> rules)
    {
        var first = rules.FirstOrDefault(x => x.BoardSync);
        if (first == null) return;
        foreach (var rule in rules)
        {
            if (!ReferenceEquals(rule, first)) rule.BoardSync = false;
            if (rule.BoardSync) rule.RequiresApproval = false;
        }
    }

    private void SyncActiveBaseUrl()
    {
        var active = _config.Profiles.FirstOrDefault(p => Eq(p.Id, _config.ActiveProfileId));
        if (active == null || string.IsNullOrWhiteSpace(active.BaseUrl)) return;
        var settings = _settings.Get();
        _settings.Set(new WcsSettingsDto { GrcsBaseUrl = active.BaseUrl, SceneName = settings.SceneName });
    }

    private static string NormalizeMethod(string? method) => method?.Trim().ToUpperInvariant() switch
    {
        "GET" => "GET", "PUT" => "PUT", "DELETE" => "DELETE", "PATCH" => "PATCH", _ => "POST",
    };
    private static bool Eq(string? left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value), JsonOptions)!;
}
