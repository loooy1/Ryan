using GrcsBackend.Modules.Wcs;
using GrcsBackend.Modules.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using GrcsBackend.Modules.Shared.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// ── 本项目定位 ──
// WCS 后端（管理面）：对 GRCS 核心系统暴露 WCS 协议回调接口（/api/v1/*），
// 对 WCS 前端提供控制台查询/管理接口（/api/wcs/*）。监听端口由 appsettings.json 的
// Urls 决定（默认 http://0.0.0.0:8230）。GRCS 核心后端（8224）是另一个系统，不在本仓库。
//
// ── 模块化约定 ──
// 每个业务域一个 Modules/<域>/ 目录（Controllers/Models/Services），
// 通过 Modules/<域>/XxxModuleExtensions.AddXxxModule() 在此挂接注册。

// 控制器 + NewtonsoftJson：GRCS 按 "yyyy-MM-dd HH:mm:ss.fff" 反序列化响应中的 MsgTime，
// 序列化端必须用同一格式，否则 GRCS 解析失败会把外围作业置为异常。
builder.Services.AddControllers().AddNewtonsoftJson(options =>
{
    options.SerializerSettings.DateFormatString = "yyyy-MM-dd HH:mm:ss.fff";
});

// CORS：允许模拟器（浏览器 WASM）调试时直接访问本服务。
// 默认允许任意来源（开发友好）；生产环境设 CORS_ORIGIN 收紧为具体域名。
var corsOrigin = Environment.GetEnvironmentVariable("CORS_ORIGIN");
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        if (!string.IsNullOrEmpty(corsOrigin))
            policy.WithOrigins(corsOrigin);
        else
            policy.SetIsOriginAllowed(_ => true);
        policy.AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

// SignalR：任务阶段事件实时推送（前端不再轮询 task-stages）
builder.Services.AddSignalR();

// 模块注册（Shared 共享基础设施 → Wcs 总模块含 Automation/Proxy/Console/Realtime 子模块）
builder.Services.AddSharedModule();
builder.Services.AddWcsModule();

var app = builder.Build();

// 全局异常兜底：未捕获异常统一返回 {"error":"..."}，避免堆栈泄露 + 前端 FriendlyError 可解析
app.Use(async (context, next) =>
{
    try { await next(); }
    catch (Exception ex)
    {
        if (context.Response.HasStarted) return;
        context.Response.StatusCode = 500;
        context.Response.ContentType = "application/json";
        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "未捕获异常");
        await context.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(new { error = ex.Message }));
    }
});

// 启动时应用未执行的 EF 迁移（wcs_inventory 等新表自动建表）
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<GrcsBackend.Modules.Shared.Infrastructure.GrcsDbContext>>().CreateDbContext();
    db.Database.Migrate();
    db.Dispose();
}

// SQLite WAL mode: readers and writers do not block each other, write conflicts queue (DefaultTimeout=30),
// fixes "database is locked" from concurrent writes (ledger init vs pick/dispatch).
try
{
    using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(app.Environment.ContentRootPath, "grcs.db")};Default Timeout=30");
    conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "PRAGMA journal_mode=WAL;";
    cmd.ExecuteNonQuery();
}
catch { }

app.UseCors();
app.MapControllers();
app.MapHub<GrcsBackend.Modules.Wcs.Realtime.TaskStageRealtimeHub>("/hubs/task-stages");

// 健康检查：/health/ready（SQLite 连通性）
app.MapGet("/health/ready", async (IDbContextFactory<GrcsDbContext> factory) =>
{
    try
    {
        await using var db = factory.CreateDbContext();
        return db.Database.CanConnect() ? Results.Ok(new { status = "ready", sqlite = "ok" }) : Results.StatusCode(503);
    }
    catch (Exception ex)
    {
        return Results.Problem(detail: ex.Message, statusCode: 503);
    }
});

app.Run();
