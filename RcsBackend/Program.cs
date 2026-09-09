using Backend.Shared;
using RCSBackend.Modules.Rcs;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// ── 本项目定位 ──
// RCS 后端（核心系统，替代 GRCS）：实现 GRCS 协议服务端接口（/api/Cargo、/api/Map/GetMap、
// /api/RawOrder/ChangeFloor、/api/v1/task_receive 等），WCS 只改 GRCS 地址配置即可切换对接。
// 对 RCS 前端/管理面提供 /api/rcs/* 管理接口。监听端口由 appsettings.json 的 Urls 决定（默认 8231）。
// 数据库：独立 rcs.db（与 WCS 的 WCS.db 隔离）。
//
// ── 模块化约定 ──
// 每个业务域一个 Modules/<域>/ 目录（Controllers/Models/Services），
// 通过 Modules/<域>/XxxModuleExtensions.AddXxxModule() 在此挂接注册。
// 共享设施（DbContext/仓储/WAL/管线）来自 Backend.Shared 类库。

// 控制器 + NewtonsoftJson：与 GRCS 协议一致的日期序列化格式（WCS 解析本服务响应依赖它）
builder.Services.AddGrcsJson();

// CORS：允许模拟器（浏览器 WASM）调试时直接访问本服务（生产环境用 CORS_ORIGIN 收紧）
builder.Services.AddGrcsCors();

// SignalR：RCS 实时推送（业务定义后注册 Hub）
builder.Services.AddSignalR();

// 共享基础设施（SQLite rcs.db + 仓储基座 + RCS 实体配置程序集；迁移在 RcsBackend 程序集）
builder.Services.AddSharedModule("rcs.db", typeof(Program).Assembly, typeof(RcsModuleExtensions).Assembly);

// 模块注册（Rcs 总模块：Protocol/Console/Realtime/Infrastructure 子模块，业务定义后填充）
builder.Services.AddRcsModule();

var app = builder.Build();

// 全局异常兜底：未捕获异常统一返回 {"error":"..."}
app.UseGlobalErrorHandler();

// 启动时应用未执行的 EF 迁移（RCS 表自动建表；首个 Add-Migration 前无迁移，CreateDatabase 兜底）
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<Backend.Shared.Infrastructure.GrcsDbContext>>().CreateDbContext();
    db.Database.Migrate();
    db.Dispose();
}

// SQLite WAL mode（与 WCS 同一套并发防护）
SqliteWal.EnsureWal(Path.Combine(app.Environment.ContentRootPath, "rcs.db"));

app.UseCors();
app.MapControllers();

// 健康检查：/health/ready（SQLite 连通性）
app.MapGrcsHealthCheck();

app.Run();