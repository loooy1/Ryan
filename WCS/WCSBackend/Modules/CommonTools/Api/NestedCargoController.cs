using Microsoft.AspNetCore.Mvc;
using WCSBackend.Modules.CommonTools.Models;
using WCSBackend.Modules.CommonTools.Services;

namespace WCSBackend.Modules.CommonTools.Api;

[ApiController]
[Route("api/common-tools/nested-cargo")]
public sealed class NestedCargoController(NestedCargoService service) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Add([FromBody] NestedCargoRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await service.AddAsync(request, cancellationToken);
            return Ok(new
            {
                success = true,
                message = $"已将货物“{result.CargoCode}”嵌套到托盘架“{result.RackCode}”。",
                result
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { success = false, message = ex.Message });
        }
        catch (MySqlConnector.MySqlException ex)
        {
            return BadRequest(new { success = false, message = $"数据库操作失败（MySQL {ex.Number}）：{ex.Message}" });
        }
    }
}
