using System.Text.Json;
using Backend.Shared.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Backend.Shared;

/// <summary>
/// 后端管线设施（WCS/RCS 共用，原 WCSBackend/Program.cs 内联段抽出）：
/// NewtonsoftJson 序列化格式（GRCS 协议硬要求）、CORS、全局异常兜底、健康检查。
/// </summary>
public static class WebPipelineExtensions
{
    /// <summary>控制器 + NewtonsoftJson：GRCS 按 "yyyy-MM-dd HH:mm:ss.fff" 反序列化响应中的 MsgTime，
    /// 序列化端必须用同一格式，否则 GRCS 解析失败会把外围作业置为异常。</summary>
    public static IMvcBuilder AddGrcsJson(this IServiceCollection services)
        => services.AddControllers().AddNewtonsoftJson(options =>
        {
            options.SerializerSettings.DateFormatString = "yyyy-MM-dd HH:mm:ss.fff";
        });

    /// <summary>CORS：允许模拟器（浏览器 WASM）调试时直接访问本服务。
    /// 默认允许任意来源（开发友好）；生产环境设 CORS_ORIGIN 收紧为具体域名。</summary>
    public static IServiceCollection AddGrcsCors(this IServiceCollection services)
    {
        var corsOrigin = Environment.GetEnvironmentVariable("CORS_ORIGIN");
        services.AddCors(options =>
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
        return services;
    }

    /// <summary>全局异常兜底：未捕获异常统一返回 {"error":"..."}，避免堆栈泄露 + 前端 FriendlyError 可解析。</summary>
    public static IApplicationBuilder UseGlobalErrorHandler(this IApplicationBuilder app)
        => app.Use(async (context, next) =>
        {
            try { await next(); }
            catch (Exception ex)
            {
                if (context.Response.HasStarted) return;
                context.Response.StatusCode = 500;
                context.Response.ContentType = "application/json";
                var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("GlobalErrorHandler");
                logger.LogError(ex, "未捕获异常");
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = ex.Message }));
            }
        });

    /// <summary>健康检查：/health/ready（SQLite 连通性）。</summary>
    public static IEndpointRouteBuilder MapGrcsHealthCheck(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/health/ready", async (IDbContextFactory<GrcsDbContext> factory) =>
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
        return endpoints;
    }
}