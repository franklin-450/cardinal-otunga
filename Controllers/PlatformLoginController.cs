using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace EduTrackTrial.Controllers
{
    [Route("__platform/core/auth-7f3a9c")]
    public class PlatformLoginController : Controller
    {
        private readonly IConfiguration _config;
        private readonly ILogger<PlatformLoginController> _logger;

        public PlatformLoginController(
            IConfiguration config,
            ILogger<PlatformLoginController> logger)
        {
            _config = config;
            _logger = logger;
        }

        [HttpGet("")]
        public IActionResult Index()
        {
            return View();
        }

        [HttpPost("")]
        public IActionResult Login(string email, string password)
        {
            if (email == _config["PlatformAdmin:Email"] &&
                password == _config["PlatformAdmin:Password"])
            {
                HttpContext.Session.SetString("PLATFORM_ADMIN", email);
                _logger.LogInformation("Platform admin login successful: {Email}", email);
                return Redirect("/__platform/core/tenants-control-7f3a9c");
            }

            _logger.LogWarning("Platform admin login failed for {Email}", email);
            ViewData["Error"] = "Invalid credentials.";
            return View("Index");
        }

        [HttpPost("logout")]
        public IActionResult Logout()
        {
            _logger.LogInformation("Platform admin logout");
            HttpContext.Session.Clear();
            return Redirect("/");
        }
    }
}
