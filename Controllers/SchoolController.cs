using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http;
using Npgsql;
using System;
using System.Net;
using System.Net.Mail;
using System.Threading;
using System.Threading.Tasks;

namespace EduTrackTrial.Controllers
{
    public class SchoolController : Controller
    {
        private readonly string _conn;
        private readonly PasswordHasher<string> _hasher;
        private readonly ILogger<SchoolController> _logger;
        private readonly IConfiguration _config;

        private const string SCHOOL_NAME = "Cardinal Otunga High School Mosocho";
        private const string SCHOOL_MOTTO = "Use Common Sense";
        private const string SCHOOL_SCHEMA = "cardinal_otunga";
        
        public SchoolController(IConfiguration config, ILogger<SchoolController> logger)
        {
            _config = config;
            _conn = config.GetConnectionString("DefaultConnection") ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
            _hasher = new PasswordHasher<string>();
            _logger = logger;
        }

        [HttpGet("login")]
        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Login()
        {
            _logger.LogInformation("Login page requested for Cardinal Otunga");

            ViewData["SchoolName"] = SCHOOL_NAME;
            ViewData["SchoolMotto"] = SCHOOL_MOTTO;
            ViewData["Error"] = TempData["Error"];
            return View("Login");
        }

        [HttpPost("login")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> LoginPost(string email, string password, CancellationToken ct)
        {
            email = email?.Trim().ToLowerInvariant() ?? string.Empty;

            _logger.LogInformation("Login attempt for Cardinal Otunga with email {Email}", email);

            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            {
                TempData["Error"] = "Email and password are required";
                return Redirect("/login");
            }

            try
            {
                await using var conn = new NpgsqlConnection(_conn);
                await conn.OpenAsync(ct);

                // Check admin credentials in the Cardinal Otunga schema
                await using var cmd = new NpgsqlCommand($@"
                    SELECT id, password_hash, full_name, role
                    FROM ""{SCHOOL_SCHEMA}"".""Admins""
                    WHERE LOWER(email) = @email
                      AND is_active = true
                    LIMIT 1", conn);

                cmd.Parameters.AddWithValue("@email", email);

                await using var reader = await cmd.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct))
                {
                    _logger.LogWarning("Failed login attempt for {Email}", email);
                    TempData["Error"] = "Invalid credentials";
                    return Redirect("/login");
                }

                int userId = reader.GetInt32(0);
                string storedHash = reader.GetString(1);
                string fullName = reader.GetString(2);
                string role = reader.GetString(3);

                var result = _hasher.VerifyHashedPassword(email, storedHash, password);
                if (result == PasswordVerificationResult.Failed)
                {
                    _logger.LogWarning("Invalid password for {Email}", email);
                    TempData["Error"] = "Invalid credentials";
                    return Redirect("/login");
                }

                // Set session data
                HttpContext.Session.SetString("user_id", userId.ToString());
                HttpContext.Session.SetString("user_email", email);
                HttpContext.Session.SetString("user_name", fullName);
                HttpContext.Session.SetString("user_role", role);
                HttpContext.Session.SetString("school_name", SCHOOL_NAME);
                HttpContext.Session.SetString("login_ts", DateTime.UtcNow.ToString("O"));

                _logger.LogInformation("Login successful for {Email} ({Role})", email, role);

                // Send confirmation email asynchronously (don't wait)
                _ = SendLoginConfirmationEmailAsync(email, fullName, role);

                // Redirect based on role
                string redirectUrl = role switch
                {
                    "Principal" => "/principal",
                    "Secretary" => "/secretary",
                    "Teacher" => "/teacher",
                    _ => "/dashboard"
                };

                return Redirect(redirectUrl);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Login error for Cardinal Otunga");
                TempData["Error"] = "An unexpected error occurred. Please try again.";
                return Redirect("/login");
            }
        }

        [HttpGet("dashboard")]
        public IActionResult Dashboard()
        {
            var userEmail = HttpContext.Session.GetString("user_email");
            if (string.IsNullOrEmpty(userEmail))
                return Redirect("/login");

            var userName = HttpContext.Session.GetString("user_name");
            var userRole = HttpContext.Session.GetString("user_role");

            ViewData["UserEmail"] = userEmail;
            ViewData["UserName"] = userName;
            ViewData["UserRole"] = userRole;
            ViewData["SchoolName"] = SCHOOL_NAME;
            ViewData["SchoolMotto"] = SCHOOL_MOTTO;

            return View("Dashboard");
        }

        [HttpPost("logout")]
        [ValidateAntiForgeryToken]
        public IActionResult Logout()
        {
            var user = HttpContext.Session.GetString("user_email");
            _logger.LogInformation("Logout for {User}", user);

            HttpContext.Session.Clear();
            TempData["Success"] = "You have been logged out successfully";
            return Redirect("/login");
        }

        private async Task SendLoginConfirmationEmailAsync(string toEmail, string fullName, string role)
        {
            try
            {
                var smtpUser = _config["Smtp:User"];
                var smtpPass = _config["Smtp:Pass"];
                var smtpHost = _config["Smtp:Host"] ?? "smtp.gmail.com";
                var smtpPort = int.Parse(_config["Smtp:Port"] ?? "587");

                if (string.IsNullOrEmpty(smtpUser) || string.IsNullOrEmpty(smtpPass))
                {
                    _logger.LogWarning("SMTP not configured. Skipping login confirmation email.");
                    return;
                }

                using var smtp = new SmtpClient(smtpHost, smtpPort)
                {
                    Credentials = new NetworkCredential(smtpUser, smtpPass),
                    EnableSsl = true
                };

                var loginTime = DateTime.Now.ToString("dddd, MMMM dd, yyyy 'at' hh:mm tt");

                var message = new MailMessage
                {
                    From = new MailAddress(smtpUser, "Cardinal Otunga EduTrack"),
                    Subject = $"Login Notification - {SCHOOL_NAME}",
                    Body = $@"
<!DOCTYPE html>
<html lang='en'>
<head>
    <meta charset='UTF-8'>
    <meta name='viewport' content='width=device-width, initial-scale=1.0'>
    <style>
        body {{
            font-family: 'Segoe UI', Tahoma, Geneva, Verdana, sans-serif;
            background-color: #f4f6f9;
            margin: 0;
            padding: 0;
        }}
        .email-container {{
            max-width: 600px;
            margin: 40px auto;
            background-color: #ffffff;
            border-radius: 12px;
            overflow: hidden;
            box-shadow: 0 4px 12px rgba(0, 0, 0, 0.1);
        }}
        .email-header {{
            background: linear-gradient(135deg, #1a3a6b 0%, #0f2545 100%);
            color: #ffffff;
            padding: 30px 20px;
            text-align: center;
        }}
        .email-header h1 {{
            margin: 0;
            font-size: 24px;
            font-weight: 700;
        }}
        .email-header p {{
            margin: 8px 0 0 0;
            font-size: 14px;
            color: #c9a961;
            font-style: italic;
        }}
        .email-body {{
            padding: 30px 25px;
            color: #333333;
            line-height: 1.7;
        }}
        .email-body h2 {{
            color: #1a3a6b;
            font-size: 20px;
            margin-bottom: 15px;
        }}
        .info-box {{
            background-color: #f8f9fa;
            border-left: 4px solid #1a3a6b;
            padding: 15px 20px;
            margin: 20px 0;
            border-radius: 6px;
        }}
        .info-box p {{
            margin: 8px 0;
            font-size: 14px;
        }}
        .info-box strong {{
            color: #1a3a6b;
        }}
        .alert-box {{
            background-color: #fff3cd;
            border-left: 4px solid #f59e0b;
            padding: 15px 20px;
            margin: 20px 0;
            border-radius: 6px;
        }}
        .alert-box p {{
            margin: 0;
            font-size: 14px;
            color: #856404;
        }}
        .email-footer {{
            background-color: #f8f9fa;
            padding: 20px;
            text-align: center;
            font-size: 12px;
            color: #6c757d;
            border-top: 1px solid #e9ecef;
        }}
        .email-footer p {{
            margin: 5px 0;
        }}
        @media only screen and (max-width: 600px) {{
            .email-container {{
                margin: 20px 10px;
            }}
            .email-header {{
                padding: 25px 15px;
            }}
            .email-body {{
                padding: 20px 15px;
            }}
        }}
    </style>
</head>
<body>
    <div class='email-container'>
        <div class='email-header'>
            <h1>{SCHOOL_NAME}</h1>
            <p>{SCHOOL_MOTTO}</p>
        </div>
        
        <div class='email-body'>
            <h2>Login Notification</h2>
            <p>Hello <strong>{fullName}</strong>,</p>
            
            <p>This is a confirmation that you have successfully logged into the Cardinal Otunga EduTrack System.</p>
            
            <div class='info-box'>
                <p><strong>Account:</strong> {toEmail}</p>
                <p><strong>Role:</strong> {role}</p>
                <p><strong>Login Time:</strong> {loginTime}</p>
            </div>
            
            <div class='alert-box'>
                <p><strong>Security Notice:</strong> If you did not initiate this login, please contact the school administration immediately and change your password.</p>
            </div>
            
            <p>Thank you for using the Cardinal Otunga EduTrack System.</p>
        </div>
        
        <div class='email-footer'>
            <p><strong>Cardinal Otunga High School Mosocho</strong></p>
            <p>Mosocho, Kisii County, Kenya</p>
            <p>&copy; {DateTime.Now.Year} Cardinal Otunga High School. All rights reserved.</p>
        </div>
    </div>
</body>
</html>",
                    IsBodyHtml = true
                };

                message.To.Add(toEmail);
                await smtp.SendMailAsync(message);

                _logger.LogInformation("Login confirmation email sent to {Email}", toEmail);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send login confirmation email to {Email}", toEmail);
                // Don't throw - email failure shouldn't prevent login
            }
        }
    }
}