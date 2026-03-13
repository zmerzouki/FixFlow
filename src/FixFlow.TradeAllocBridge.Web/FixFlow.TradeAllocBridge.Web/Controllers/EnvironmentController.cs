using Microsoft.AspNetCore.Mvc;

namespace FixFlow.TradeAllocBridge.Web.Controllers;

[ApiController]
[Route("api/environment")]
public class EnvironmentController : ControllerBase
{
    private readonly IWebHostEnvironment _environment;

    public EnvironmentController(IWebHostEnvironment environment)
    {
        _environment = environment;
    }

    [HttpGet]
    public ActionResult<string> Get()
    {
        return Ok(_environment.EnvironmentName);
    }
}
