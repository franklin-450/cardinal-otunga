using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using EduTrackTrial.Models;

namespace EduTrackTrial.Controllers;

public class HomeController : Controller
{
    private const string SCHOOL_NAME = "Cardinal Otunga High School Mosocho";
    private const string SCHOOL_MOTTO = "Use Common Sense";
    private const string SCHOOL_COLORS = "#8B1538,#FFD700"; // Maroon and Gold
    private const string SCHOOL_SUBDOMAIN = "cardinalotunga";

    public IActionResult Index()
    {
        ViewData["SchoolName"] = SCHOOL_NAME;
        ViewData["SchoolMotto"] = SCHOOL_MOTTO;
        return View();
    }

    public IActionResult Privacy()
    {
        ViewData["SchoolName"] = SCHOOL_NAME;
        return View();
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
    
    // Legacy redirects for old multi-tenant routes - redirect to Cardinal Otunga
    [HttpGet("/{schoolName}/login")]
    public IActionResult RedirectToLogin(string schoolName)
    {
        return Redirect("/login");
    }
    
    [HttpGet("/{schoolName}/admission")]
    public IActionResult RedirectToAdmission(string schoolName)
    {
        return Redirect($"/{SCHOOL_SUBDOMAIN}/admission");
    }
    
    [HttpGet("/{schoolName}/principal")]
    public IActionResult RedirectToPrincipal(string schoolName)
    {
        return Redirect($"/{SCHOOL_SUBDOMAIN}/principal");
    }
    
    [HttpGet("/{schoolName}/secretary")]
    public IActionResult RedirectToSecretary(string schoolName)
    {
        return Redirect($"/{SCHOOL_SUBDOMAIN}/secretary");
    }
    
    [HttpGet("/{schoolName}/parent")]
    public IActionResult RedirectToParent(string schoolName)
    {
        return Redirect("/parent/index");
    }
}