using Backend.Shared;
using Backend.Shared.Logging;
using Contracts.Rcs.Tasks;
using RCSBackend.Modules.Rcs;

var builder = WebApplication.CreateBuilder(args);
builder.AddSharedLogging();

// ── 本项目定位 ──
// 自研 RCS 后端：地图、算法、任务接收/调度及虚拟车；尚未实现完整 GRCS 库存和回调协议。
// 对 RCS 前端/管理面提供 /api/rcs/* 管理接口。监听端口由 appsettings.json 的 Urls 决定（默认 8232）。
// 数据库：独立 MySQL 数据库（与 WCS 数据库隔离）。
//
// ── 模块化约定 ──
// 每个业务域一个 Modules/<域>/ 目录（Controllers/Models/Services），
// 通过 Modules/<域>/XxxModuleExtensions.AddXxxModule() 在此挂接注册。
// 共享设施（DbContext/仓储/管线）来自 Backend.Shared 类库。

// 控制器 + NewtonsoftJson：与 GRCS 协议一致的日期序列化格式（WCS 解析本服务响应依赖它）
builder.Services.AddGrcsJson();

// CORS：允许模拟器（浏览器 WASM）调试时直接访问本服务（生产环境用 CORS_ORIGIN 收紧）
builder.Services.AddGrcsCors();
builder.Services.AddSharedHttpClientLogging();

// SignalR：车辆状态、任务变化和日志实时推送。
builder.Services.AddSignalR();

// 共享基础设施：MySQL。RCS 地图与任务实体由本项目迁移初始化。
builder.Services.AddSharedModule(
    builder.Configuration,
    new SharedDatabaseRegistration(
        "RcsDatabase",
        typeof(Program).Assembly),
    typeof(RcsModuleExtensions).Assembly);

// 注册地图、任务调度、虚拟车和实时发布模块。
builder.Services.AddRcsModule();

var app = builder.Build();
app.Logger.LogInformation("RCS 后端启动，环境={Environment}", app.Environment.EnvironmentName);

// 全局异常兜底：未捕获异常统一返回 {"error":"..."}
app.UseSharedHttpLogging();
app.Use(async (context, next) =>
{
    try { await next(); }
    catch (Exception ex)
    {
        if (context.Response.HasStarted) throw;
        context.Response.Clear();
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        app.Logger.LogError(ex, "未处理异常：{Method} {Path}", context.Request.Method, context.Request.Path);
        await context.Response.WriteAsJsonAsync(RcsApiResponse.Rejected(ex.Message));
    }
});

await app.InitializeSharedDatabaseAsync();
await app.Services.GetRequiredService<RCSBackend.Modules.Rcs.Application.Execution.RcsAlgorithmSettingsService>().InitializeAsync();
await app.Services.GetRequiredService<RCSBackend.Modules.Rcs.Application.StationBusiness.RcsStationBusinessRuleService>().InitializeAsync();
await app.Services.GetRequiredService<RCSBackend.Modules.Rcs.Application.Simulation.RcsSimulationService>().ReloadMapAsync();

app.UseCors();
app.MapControllers();
app.MapHub<RCSBackend.Modules.Rcs.Realtime.RcsRealtimeHub>("/hubs/rcs-realtime");

// 健康检查：/RCS_ready（当前数据库连通性）
app.MapGrcsHealthCheck("/RCS_ready");

app.Lifetime.ApplicationStopping.Register(() =>
    app.Logger.LogInformation("RCS 后端正在停止"));

app.Run();
