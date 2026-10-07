using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RCSBackend.Modules.Rcs.Api;
using RCSBackend.Modules.Rcs.Application.Inventory;

namespace Rcs.Architecture.Tests;

[TestClass]
public sealed class InventoryErrorMappingTests
{
    [DataTestMethod]
    [DataRow(RcsInventoryErrorKind.Invalid, 400)]
    [DataRow(RcsInventoryErrorKind.Conflict, 409)]
    [DataRow(RcsInventoryErrorKind.NotFound, 404)]
    public void CatalogErrorsKeepHttpStatus(RcsInventoryErrorKind kind, int expectedStatus)
    {
        var action = new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor());
        var context = new ExceptionContext(action, [])
        {
            Exception = new RcsInventoryCatalogException(kind, "库存错误")
        };

        new RcsInventoryExceptionFilter().OnException(context);

        Assert.IsTrue(context.ExceptionHandled);
        Assert.IsInstanceOfType<ObjectResult>(context.Result);
        Assert.AreEqual(expectedStatus, ((ObjectResult)context.Result).StatusCode);
    }
}
