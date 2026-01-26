using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Npgsql;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Mail;
using System.Threading.Tasks;

namespace EduTrackTrial.Controllers
{
    [Route("{schoolName}/secretary")]
    public class SecretaryController : Controller
    {
        private readonly string _conn;
        private readonly ILogger<SecretaryController> _logger;
        private readonly IConfiguration _config;
        private readonly PasswordHasher<string> _hasher;

        public SecretaryController(IConfiguration config, ILogger<SecretaryController> logger)
        {
            _conn = config.GetConnectionString("DefaultConnection") ?? "";
            _logger = logger;
            _config = config;
            _hasher = new PasswordHasher<string>();
        }

        // =========================
        // CHECK IF FIRST TIME LOGIN
        // =========================
        [HttpGet("check-status")]
        public async Task<IActionResult> CheckStatus(string schoolName)
        {
            try
            {
                var (tenantId, schema) = await ResolveTenantAsync(schoolName);

                await using var conn = new NpgsqlConnection(_conn);
                await conn.OpenAsync();

                // Check if any secretary exists
                await using var cmd = new NpgsqlCommand(
                    $@"SELECT COUNT(*) FROM ""{schema}"".""Secretaries"" LIMIT 1;", conn);

                int count = Convert.ToInt32(await cmd.ExecuteScalarAsync());

                return Json(new { success = true, isFirstTime = count == 0 });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking secretary status for {SchoolName}", schoolName);
                return Json(new { success = false, isFirstTime = true });
            }
        }

        // =========================
        // DASHBOARD PAGE
        // =========================
        [HttpGet("")]
        [HttpGet("index")]
        public async Task<IActionResult> Index(string schoolName)
        {
            var secretaryEmail = HttpContext.Session.GetString("secretary_email");

            if (string.IsNullOrEmpty(secretaryEmail))
            {
                return RedirectToAction(nameof(Login), new { schoolName });
            }

            try
            {
                var (tenantId, schema) = await ResolveTenantAsync(schoolName);

                ViewData["SchoolName"] = schoolName;
                ViewData["SecretaryEmail"] = secretaryEmail;

                return View("Index");
            }
            catch
            {
                return RedirectToAction(nameof(Login), new { schoolName });
            }
        }

        // =========================
        // LOGIN PAGE
        // =========================
        [HttpGet("login")]
        public async Task<IActionResult> Login(string schoolName)
        {
            var (tenantId, schema) = await ResolveTenantAsync(schoolName);

            await using var conn = new NpgsqlConnection(_conn);
            await conn.OpenAsync();

            // Check if this is first time
            await using var cmd = new NpgsqlCommand(
                $@"SELECT COUNT(*) FROM ""{schema}"".""Secretaries"" LIMIT 1;", conn);

            int count = Convert.ToInt32(await cmd.ExecuteScalarAsync());

            ViewData["SchoolName"] = schoolName;
            ViewData["IsFirstTime"] = count == 0;

            return View("Login");
        }

        // =========================
        // LOGIN POST
        // =========================
        [HttpPost("login")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> LoginPost(string schoolName, string email, string password)
        {
            if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password))
            {
                TempData["Error"] = "Email and password are required";
                return RedirectToAction(nameof(Login), new { schoolName });
            }

            try
            {
                var (tenantId, schema) = await ResolveTenantAsync(schoolName);

                await using var conn = new NpgsqlConnection(_conn);
                await conn.OpenAsync();

                await using var cmd = new NpgsqlCommand($@"
                    SELECT id, password_hash, full_name
                    FROM ""{schema}"".""Secretaries""
                    WHERE LOWER(email) = LOWER(@email)
                    LIMIT 1;", conn);

                cmd.Parameters.AddWithValue("@email", email.Trim().ToLowerInvariant());

                await using var reader = await cmd.ExecuteReaderAsync();
                if (!await reader.ReadAsync())
                {
                    _logger.LogWarning("Failed login attempt for secretary {Email} at {SchoolName}", email, schoolName);
                    TempData["Error"] = "Invalid email or password";
                    return RedirectToAction(nameof(Login), new { schoolName });
                }

                int secretaryId = reader.GetInt32(0);
                string storedHash = reader.GetString(1);
                string secretaryName = reader.GetString(2);

                var result = _hasher.VerifyHashedPassword(email, storedHash, password);
                if (result == PasswordVerificationResult.Failed)
                {
                    TempData["Error"] = "Invalid email or password";
                    return RedirectToAction(nameof(Login), new { schoolName });
                }

                // Set session
                HttpContext.Session.SetString("secretary_email", email);
                HttpContext.Session.SetString("secretary_name", secretaryName);
                HttpContext.Session.SetString("secretary_id", secretaryId.ToString());

                _logger.LogInformation("Secretary login successful: {Email} at {SchoolName}", email, schoolName);

                return RedirectToAction(nameof(Index), new { schoolName });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Login error for {SchoolName}", schoolName);
                TempData["Error"] = "Login failed. Please try again.";
                return RedirectToAction(nameof(Login), new { schoolName });
            }
        }

        // =========================
        // REGISTER SECRETARY (Principal Only)
        // =========================
        [HttpPost("register")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RegisterSecretary(string schoolName, [FromBody] SecretaryRegistrationRequest request)
        {
            if (string.IsNullOrEmpty(request?.Email) || string.IsNullOrEmpty(request?.Name))
            {
                return Json(new { success = false, message = "Name and email are required" });
            }

            try
            {
                var (tenantId, schema) = await ResolveTenantAsync(schoolName);

                await using var conn = new NpgsqlConnection(_conn);
                await conn.OpenAsync();

                // Check if table exists, if not create it
                await using var tableCmd = new NpgsqlCommand($@"
                    CREATE TABLE IF NOT EXISTS ""{schema}"".""Secretaries"" (
                        id SERIAL PRIMARY KEY,
                        name TEXT NOT NULL,
                        email VARCHAR(100) UNIQUE NOT NULL,
                        phone VARCHAR(20),
                        password_hash TEXT NOT NULL,
                        created_at TIMESTAMP DEFAULT NOW()
                    );", conn);
                await tableCmd.ExecuteNonQueryAsync();

                // Generate random password
                string generatedPassword = GenerateSecurePassword();
                string passwordHash = _hasher.HashPassword(request.Email, generatedPassword);

                // Insert secretary
                await using var insertCmd = new NpgsqlCommand($@"
                    INSERT INTO ""{schema}"".""Secretaries""
                    (name, email, phone, password_hash, created_at)
                    VALUES (@name, @email, @phone, @hash, NOW())
                    RETURNING id;", conn);

                insertCmd.Parameters.AddWithValue("@name", request.Name);
                insertCmd.Parameters.AddWithValue("@email", request.Email.ToLowerInvariant());
                insertCmd.Parameters.AddWithValue("@phone", request.Phone ?? "");
                insertCmd.Parameters.AddWithValue("@hash", passwordHash);

                var result = await insertCmd.ExecuteScalarAsync();
                if (result == null)
                {
                    return Json(new { success = false, message = "Failed to register secretary" });
                }
                int secretaryId = (int)result;

                // Send email with credentials
                await SendSecretaryCredentialsEmailAsync(request.Email, request.Name, generatedPassword, schoolName);

                _logger.LogInformation("Secretary registered: {Email} (ID: {SecretaryId}) at {SchoolName}",
                    request.Email, secretaryId, schoolName);

                return Json(new
                {
                    success = true,
                    message = $"Secretary registered. Login credentials sent to {request.Email}",
                    secretaryId
                });
            }
            catch (PostgresException pex) when (pex.SqlState == "23505")
            {
                return Json(new { success = false, message = "Email already registered" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Secretary registration error for {SchoolName}", schoolName);
                return Json(new { success = false, message = "Registration failed" });
            }
        }

        // =========================
        // DASHBOARD STATS API
        // =========================
        [HttpGet("api/dashboard-stats")]
        public async Task<IActionResult> GetDashboardStats(string schoolName)
        {
            var secretaryEmail = HttpContext.Session.GetString("secretary_email");
            if (string.IsNullOrEmpty(secretaryEmail))
            {
                return Json(new { success = false, message = "Unauthorized" });
            }

            try
            {
                var (tenantId, schema) = await ResolveTenantAsync(schoolName);

                await using var conn = new NpgsqlConnection(_conn);
                await conn.OpenAsync();

                // Total collected fees
                decimal totalCollected = 0;
                await using (var cmd = new NpgsqlCommand(
                    $@"SELECT COALESCE(SUM(amount), 0) FROM ""{schema}"".""Payments"" WHERE status = 'Completed';", conn))
                {
                    totalCollected = Convert.ToDecimal(await cmd.ExecuteScalarAsync());
                }

                // Total students
                int totalStudents = 0;
                await using (var cmd = new NpgsqlCommand(
                    $@"SELECT COUNT(*) FROM ""{schema}"".""Students"" WHERE status = 'Active';", conn))
                {
                    totalStudents = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                }

                // Fully paid students
                int paidStudents = 0;
                await using (var cmd = new NpgsqlCommand($@"
                    SELECT COUNT(DISTINCT s.id)
                    FROM ""{schema}"".""Students"" s
                    JOIN ""{schema}"".""Grades"" g ON s.grade = g.grade_name
                    WHERE (COALESCE(SUM(p.amount), 0) >= (g.term1_fee + g.term2_fee + g.term3_fee))
                    GROUP BY s.id;", conn))
                {
                    await using var reader = await cmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                        paidStudents++;
                }

                // Pending payments
                decimal pendingAmount = 0;
                await using (var cmd = new NpgsqlCommand($@"
                    SELECT COALESCE(SUM((g.term1_fee + g.term2_fee + g.term3_fee) - COALESCE(SUM(p.amount), 0)), 0)
                    FROM ""{schema}"".""Students"" s
                    JOIN ""{schema}"".""Grades"" g ON s.grade = g.grade_name
                    LEFT JOIN ""{schema}"".""Payments"" p ON s.id = p.student_id AND p.status = 'Completed'
                    GROUP BY s.id;", conn))
                {
                    pendingAmount = Convert.ToDecimal(await cmd.ExecuteScalarAsync());
                }

                return Json(new
                {
                    success = true,
                    totalCollected,
                    totalStudents,
                    paidStudents,
                    pendingAmount,
                    percentagePaid = totalStudents > 0 ? (paidStudents * 100) / totalStudents : 0
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting dashboard stats for {SchoolName}", schoolName);
                return Json(new { success = false, message = "Error retrieving stats" });
            }
        }

        // =========================
        // GET ALL STUDENTS WITH FEES
        // =========================
        [HttpGet("api/students-fees")]
        public async Task<IActionResult> GetStudentsFees(string schoolName)
        {
            var secretaryEmail = HttpContext.Session.GetString("secretary_email");
            if (string.IsNullOrEmpty(secretaryEmail))
            {
                return Json(new { success = false, message = "Unauthorized" });
            }

            try
            {
                var (tenantId, schema) = await ResolveTenantAsync(schoolName);

                await using var conn = new NpgsqlConnection(_conn);
                await conn.OpenAsync();

                var students = new List<dynamic>();

                await using (var cmd = new NpgsqlCommand($@"
                    SELECT 
                        s.id,
                        s.account_no,
                        s.full_name,
                        s.grade,
                        s.stream,
                        g.term1_fee + g.term2_fee + g.term3_fee as total_fee,
                        COALESCE(SUM(p.amount), 0) as amount_paid,
                        s.status
                    FROM ""{schema}"".""Students"" s
                    LEFT JOIN ""{schema}"".""Grades"" g ON s.grade = g.grade_name
                    LEFT JOIN ""{schema}"".""Payments"" p ON s.id = p.student_id AND p.status = 'Completed'
                    WHERE s.status = 'Active'
                    GROUP BY s.id, g.term1_fee, g.term2_fee, g.term3_fee
                    ORDER BY s.full_name;", conn))
                await using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        decimal totalFee = reader.GetDecimal(5);
                        decimal amountPaid = reader.GetDecimal(6);
                        decimal balance = totalFee - amountPaid;

                        students.Add(new
                        {
                            id = reader.GetInt32(0),
                            accountNo = reader.GetString(1),
                            fullName = reader.GetString(2),
                            grade = reader.GetString(3),
                            stream = reader.IsDBNull(4) ? "-" : reader.GetString(4),
                            totalFee,
                            amountPaid,
                            balance = balance > 0 ? balance : 0,
                            paymentStatus = balance <= 0 ? "Paid" : balance >= totalFee ? "Pending" : "Partial"
                        });
                    }
                }

                return Json(new { success = true, students });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting student fees for {SchoolName}", schoolName);
                return Json(new { success = false, message = "Error retrieving data" });
            }
        }

        // =========================
        // RECORD PAYMENT
        // =========================
        [HttpPost("api/record-payment")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RecordPayment(string schoolName, [FromBody] PaymentRecordRequest request)
        {
            var secretaryEmail = HttpContext.Session.GetString("secretary_email");
            if (string.IsNullOrEmpty(secretaryEmail))
            {
                return Json(new { success = false, message = "Unauthorized" });
            }

            if (request == null || request.StudentId <= 0 || request.Amount <= 0)
            {
                return Json(new { success = false, message = "Invalid student or amount" });
            }

            try
            {
                var (tenantId, schema) = await ResolveTenantAsync(schoolName);

                await using var conn = new NpgsqlConnection(_conn);
                await conn.OpenAsync();

                // Create payments table if not exists
                await using var tableCmd = new NpgsqlCommand($@"
                    CREATE TABLE IF NOT EXISTS ""{schema}"".""Payments"" (
                        id SERIAL PRIMARY KEY,
                        student_id INT NOT NULL REFERENCES ""{schema}"".""Students""(id),
                        amount DECIMAL(10,2) NOT NULL,
                        payment_method VARCHAR(50),
                        status VARCHAR(20) DEFAULT 'Completed',
                        reference VARCHAR(100),
                        notes TEXT,
                        recorded_by INT REFERENCES ""{schema}"".""Secretaries""(id),
                        created_at TIMESTAMP DEFAULT NOW()
                    );", conn);
                await tableCmd.ExecuteNonQueryAsync();

                int secretaryId = int.Parse(HttpContext.Session.GetString("secretary_id") ?? "0");

                // Insert payment
                await using var cmd = new NpgsqlCommand($@"
                    INSERT INTO ""{schema}"".""Payments""
                    (student_id, amount, payment_method, status, reference, notes, recorded_by, created_at)
                    VALUES (@studentId, @amount, @method, 'Completed', @ref, @notes, @secretaryId, NOW())
                    RETURNING id;", conn);

                cmd.Parameters.AddWithValue("@studentId", request.StudentId);
                cmd.Parameters.AddWithValue("@amount", request.Amount);
                cmd.Parameters.AddWithValue("@method", request.PaymentMethod ?? "Cash");
                cmd.Parameters.AddWithValue("@ref", request.Reference ?? "");
                cmd.Parameters.AddWithValue("@notes", request.Notes ?? "");
                cmd.Parameters.AddWithValue("@secretaryId", secretaryId);

                var result = await cmd.ExecuteScalarAsync();
                if (result == null)
                {
                    return Json(new { success = false, message = "Failed to record payment" });
                }
                int paymentId = (int)result;

                _logger.LogInformation("Payment recorded: {PaymentId} for student {StudentId} at {SchoolName}",
                    paymentId, request.StudentId, schoolName);

                return Json(new
                {
                    success = true,
                    paymentId,
                    message = $"Payment of {request.Amount} recorded successfully"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error recording payment for {SchoolName}", schoolName);
                return Json(new { success = false, message = "Failed to record payment" });
            }
        }

        // =========================
        // GET BUDGET DATA
        // =========================
        [HttpGet("api/budget")]
        public async Task<IActionResult> GetBudget(string schoolName)
        {
            var secretaryEmail = HttpContext.Session.GetString("secretary_email");
            if (string.IsNullOrEmpty(secretaryEmail))
            {
                return Json(new { success = false, message = "Unauthorized" });
            }

            try
            {
                var (tenantId, schema) = await ResolveTenantAsync(schoolName);

                await using var conn = new NpgsqlConnection(_conn);
                await conn.OpenAsync();

                var budgetData = new List<dynamic>();

                await using (var cmd = new NpgsqlCommand($@"
                    SELECT 
                        g.grade_name,
                        COUNT(DISTINCT s.id) as student_count,
                        g.term1_fee + g.term2_fee + g.term3_fee as term_fee,
                        COALESCE(SUM(p.amount), 0) as collected
                    FROM ""{schema}"".""Grades"" g
                    LEFT JOIN ""{schema}"".""Students"" s ON s.grade = g.grade_name AND s.status = 'Active'
                    LEFT JOIN ""{schema}"".""Payments"" p ON s.id = p.student_id AND p.status = 'Completed'
                    GROUP BY g.grade_name, g.term1_fee, g.term2_fee, g.term3_fee
                    ORDER BY g.grade_name;", conn))
                await using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        int studentCount = reader.GetInt32(1);
                        decimal termFee = reader.GetDecimal(2);
                        decimal collected = reader.GetDecimal(3);
                        decimal expected = termFee * studentCount;
                        decimal pending = expected - collected;

                        budgetData.Add(new
                        {
                            grade = reader.GetString(0),
                            studentCount,
                            termFee,
                            expectedRevenue = expected,
                            collectedRevenue = collected,
                            pendingRevenue = pending > 0 ? pending : 0
                        });
                    }
                }

                return Json(new { success = true, budgetData });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting budget for {SchoolName}", schoolName);
                return Json(new { success = false, message = "Error retrieving budget" });
            }
        }

        // =========================
        // LOGOUT
        // =========================
        [HttpPost("logout")]
        [ValidateAntiForgeryToken]
        public IActionResult Logout(string schoolName)
        {
            HttpContext.Session.Remove("secretary_email");
            HttpContext.Session.Remove("secretary_name");
            HttpContext.Session.Remove("secretary_id");

            return RedirectToAction(nameof(Login), new { schoolName });
        }

        // =========================
        // HELPER METHODS
        // =========================
        private async Task<(int tenantId, string schema)> ResolveTenantAsync(string school)
        {
            await using var conn = new NpgsqlConnection(_conn);
            await conn.OpenAsync();

            await using var cmd = new NpgsqlCommand(
                "SELECT id, subdomain FROM tenants WHERE LOWER(subdomain)=@s", conn);
            cmd.Parameters.AddWithValue("@s", school.ToLower());

            await using var r = await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync())
            {
                _logger.LogError("Tenant not found: {SchoolName}", school);
                throw new Exception("Tenant not found");
            }

            return (r.GetInt32(0), $"tenant_{r.GetString(1)}");
        }

        private string GenerateSecurePassword()
        {
            const string validChars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789!@#$%";
            var random = new Random();
            var password = new char[12];

            for (int i = 0; i < password.Length; i++)
            {
                password[i] = validChars[random.Next(validChars.Length)];
            }

            return new string(password);
        }

        private async Task SendSecretaryCredentialsEmailAsync(string email, string name, string password, string schoolName)
        {
            try
            {
                var smtpUser = _config["Smtp:User"];
                var smtpPass = _config["Smtp:Pass"];

                if (string.IsNullOrEmpty(smtpUser) || string.IsNullOrEmpty(smtpPass))
                    return;

                using var smtp = new SmtpClient("smtp.gmail.com", 587)
                {
                    Credentials = new NetworkCredential(smtpUser, smtpPass),
                    EnableSsl = true
                };

                var loginUrl = $"http://localhost:5201/{schoolName}/secretary/login";

                var message = new MailMessage
                {
                    From = new MailAddress(smtpUser, "EduTrack"),
                    Subject = "Your School Secretary Account Credentials",
                    IsBodyHtml = true,
                    Body = $@"
<!DOCTYPE html>
<html>
<head>
<style>
body {{ font-family: Arial, sans-serif; background: #f4f4f4; }}
.container {{ max-width: 600px; margin: 20px auto; background: white; padding: 20px; border-radius: 10px; }}
.header {{ background: #2E86C1; color: white; padding: 15px; border-radius: 5px; margin-bottom: 20px; }}
.content {{ color: #333; line-height: 1.6; }}
.credentials {{ background: #f9f9f9; padding: 15px; border-left: 4px solid #2E86C1; margin: 20px 0; }}
.credentials p {{ margin: 10px 0; }}
.code {{ font-family: monospace; background: #f0f0f0; padding: 5px 10px; border-radius: 3px; }}
.btn {{ display: inline-block; background: #2E86C1; color: white; padding: 10px 20px; text-decoration: none; border-radius: 5px; margin-top: 10px; }}
</style>
</head>
<body>
<div class='container'>
    <div class='header'>
        <h2>Welcome to EduTrack School Secretary Portal</h2>
    </div>
    <div class='content'>
        <p>Hello <strong>{name}</strong>,</p>
        
        <p>You have been registered as a school secretary for <strong>{schoolName}</strong>. Your account has been created and is ready to use.</p>

        <div class='credentials'>
            <p><strong>Login Email:</strong> <span class='code'>{email}</span></p>
            <p><strong>Temporary Password:</strong> <span class='code'>{password}</span></p>
        </div>

        <p><strong>Please keep this information secure and change your password after first login.</strong></p>

        <a href='{loginUrl}' class='btn'>Login to Dashboard</a>

        <p style='margin-top: 30px; color: #888; font-size: 12px;'>If you did not request this account, please contact your administrator immediately.</p>
    </div>
</div>
</body>
</html>"
                };

                message.To.Add(email);
                await smtp.SendMailAsync(message);

                _logger.LogInformation("Secretary credentials email sent to {Email}", email);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send secretary credentials email to {Email}", email);
            }
        }
    }

    // =========================
    // DTOs
    // =========================
    public class SecretaryRegistrationRequest
    {
        public required string Name { get; set; }
        public required string Email { get; set; }
        public string? Phone { get; set; }
    }

    public class PaymentRecordRequest
    {
        public int StudentId { get; set; }
        public decimal Amount { get; set; }
        public string? PaymentMethod { get; set; }
        public string? Reference { get; set; }
        public string? Notes { get; set; }
    }
}