using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Identity;
using Npgsql;
using NpgsqlTypes;
using System;
using System.Net;
using System.Net.Mail;
using System.Collections.Generic;
using EduTrackTrial.Models;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.ComponentModel.DataAnnotations;  // ← ADD THIS LINE


namespace EduTrackTrial.Controllers
{
    public class SubscriptionController : Controller
    {
        private readonly string _conn;
        private readonly IConfiguration _config;
        private readonly PasswordHasher<string> _hasher;
public SubscriptionController(IConfiguration config)
{
    // Ensure configuration is not null
    _config = config ?? throw new ArgumentNullException(nameof(config));

    // Load connection string from appsettings.json
    _conn = _config.GetConnectionString("DefaultConnection") ?? throw new InvalidOperationException("DefaultConnection is not configured in appsettings.json.");

    // Initialize password hasher
    _hasher = new PasswordHasher<string>();
}


        // =========================
        // SUBSCRIBE PAGE
        // =========================
        [HttpGet("/subscribe")]
        public IActionResult Index()
        {
            Console.WriteLine("[SUBSCRIBE] Page accessed");
            return View();
        }

        // =========================
        // SUBSCRIBE POST
        // =========================
        [HttpPost("/subscribe")]
        public IActionResult Subscribe([FromBody] SubscriptionRequest request)
        {
            Console.WriteLine($"[SUBSCRIBE] Attempt: {request?.SchoolName}");

            if (request == null)
                return Json(new { success = false, message = "Invalid request." });

            if (string.IsNullOrWhiteSpace(request.SchoolGender))
                return Json(new { success = false, message = "School gender is required." });

            if (string.IsNullOrWhiteSpace(request.SchoolName) ||
                string.IsNullOrWhiteSpace(request.AdminEmail) ||
                string.IsNullOrWhiteSpace(request.Password) ||
                request.PlanAmount <= 0 ||
                request.Grades == null || request.Grades.Count == 0)
                return Json(new { success = false, message = "Missing required fields or grades." });

            Console.WriteLine($"[SUBSCRIBE] Grades received: {request.Grades.Count}");
            foreach (var g in request.Grades)
            {
                Console.WriteLine($"  - Grade: {g.Name}, Term1: {g.Fees?.Term1}, Streams: {string.Join(",", g.Streams ?? new())}");
            }

            // Validate grades have required data
            foreach (var g in request.Grades)
            {
                if (string.IsNullOrWhiteSpace(g.Name))
                {
                    Console.WriteLine($"[SUBSCRIBE] ERROR: Grade name is empty/null!");
                    return Json(new { success = false, message = "Grade name cannot be empty." });
                }
                if (g.Fees == null)
                {
                    Console.WriteLine($"[SUBSCRIBE] ERROR: Fees object is null for grade '{g.Name}'!");
                    return Json(new { success = false, message = $"Fees required for grade '{g.Name}'." });
                }
            }

            using var conn = new NpgsqlConnection(_conn);
            conn.Open();

            try
            {
                // =========================
                // Determine lowest available ID
                // =========================
                int tenantId;
                using (var idCmd = new NpgsqlCommand(@"
                    SELECT COALESCE(
                        (SELECT MIN(t1.id + 1)
                         FROM tenants t1
                         WHERE NOT EXISTS (
                             SELECT 1 FROM tenants t2 WHERE t2.id = t1.id + 1
                         )
                        ), 1) AS next_id;
                ", conn))
                {
                    var result = idCmd.ExecuteScalar();
                    tenantId = (result != DBNull.Value) ? Convert.ToInt32(result) : 1;
                }

                Console.WriteLine($"[SUBSCRIBE] Tenant ID assigned: {tenantId}");

                // =========================
                // Prepare school data
                // =========================
                string schoolName = request.SchoolName.Trim();
                string subdomain = SanitizeSubdomain(schoolName);

                Console.WriteLine($"[SUBSCRIBE] School: {schoolName}, Subdomain: {subdomain}");

                if (SubdomainExists(subdomain, conn))
                    return Json(new { success = false, message = "School already exists." });

                string email = request.AdminEmail.Trim().ToLowerInvariant();
                string passwordHash = _hasher.HashPassword(email, request.Password);
                string verificationCode = GenerateCode();

                Console.WriteLine($"[SUBSCRIBE] Email: {email}, Code: {verificationCode}");

                // =========================
                // INSERT TENANT
                // =========================
                var tenantCmd = new NpgsqlCommand(@"
INSERT INTO tenants
(id, name, subdomain, email, password_hash, plan_amount, trial_expires_at, verified, school_gender, created_at)
VALUES (@id, @name, @sub, @email, @hash, @plan, @trial, false, @gender, NOW())
RETURNING id;", conn);

                tenantCmd.Parameters.AddWithValue("id", tenantId);
                tenantCmd.Parameters.AddWithValue("name", schoolName);
                tenantCmd.Parameters.AddWithValue("sub", subdomain);
                tenantCmd.Parameters.AddWithValue("email", email);
                tenantCmd.Parameters.AddWithValue("hash", passwordHash);
                tenantCmd.Parameters.AddWithValue("plan", request.PlanAmount);
                tenantCmd.Parameters.AddWithValue("trial", DateTime.UtcNow.AddDays(7));
                tenantCmd.Parameters.AddWithValue("gender", request.SchoolGender);

                tenantId = (int)tenantCmd.ExecuteScalar();
                Console.WriteLine($"[SUBSCRIBE] Tenant inserted with ID: {tenantId}");

                // =========================
                // SERIALIZE AND VALIDATE GRADES
                // =========================
                var gradesJson = JsonSerializer.Serialize(request.Grades);
                Console.WriteLine($"[SUBSCRIBE] Grades JSON being stored: {gradesJson}");
                Console.WriteLine($"[SUBSCRIBE] Grades JSON length: {gradesJson.Length} characters");

                // Test deserialization immediately
                var testDeserialize = JsonSerializer.Deserialize<List<GradeSeedDto>>(gradesJson);
                Console.WriteLine($"[SUBSCRIBE] Test deserialization successful: {testDeserialize?.Count ?? 0} grades");
                if (testDeserialize != null)
                {
                    foreach (var tg in testDeserialize)
                    {
                        Console.WriteLine($"  - Deserialized Grade: Name='{tg.Name}', Fees={tg.Fees?.Term1}, Streams={string.Join(",", tg.Streams ?? new())}");
                    }
                }

                // =========================
                // STORE VERIFICATION CODE + GRADES IN DATABASE
                // =========================
                var verCmd = new NpgsqlCommand(@"
INSERT INTO tenant_verifications (tenant_id, code, expires_at, grades_json)
VALUES (@tid, @code, @exp, @grades)
ON CONFLICT (tenant_id)
DO UPDATE SET code = EXCLUDED.code, expires_at = EXCLUDED.expires_at, grades_json = EXCLUDED.grades_json;", conn);

                verCmd.Parameters.AddWithValue("tid", tenantId);
                verCmd.Parameters.AddWithValue("code", verificationCode);
                verCmd.Parameters.AddWithValue("exp", DateTime.UtcNow.AddMinutes(10));
                verCmd.Parameters.AddWithValue("grades", gradesJson);
                verCmd.ExecuteNonQuery();

                Console.WriteLine($"[SUBSCRIBE] Verification record inserted with grades JSON");

                // =========================
                // EMAIL VERIFICATION
                // =========================
                if (SmtpConfigured())
                {
                    SendVerificationEmail(
                        email,
                        schoolName,
                        verificationCode,
                        subdomain,
                        request.PlanAmount
                    );
                }

                return Json(new
                {
                    success = true,
                    tenantId,
                    subdomain,
                    message = "Verification code sent. Trial activated for 7 days."
                });
            }
            catch (PostgresException pg)
            {
                Console.WriteLine($"[DB ERROR] {pg.Message}");
                return Json(new { success = false, message = "School already exists." });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] {ex.Message}");
                Console.WriteLine($"[ERROR STACKTRACE] {ex.StackTrace}");
                return Json(new { success = false, message = "Subscription failed." });
            }
        }

        // =========================
        // VERIFY ACCOUNT
        // =========================
        [HttpGet("/subscribe/verify")]
        public IActionResult Verify(string email, string code)
        {
            Console.WriteLine($"[VERIFY] Starting verification for email: {email}, code: {code}");

            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(code))
                return Content("Missing email or verification code.");

            email = email.Trim().ToLowerInvariant();
            Console.WriteLine($"[VERIFY] Normalized email: {email}");

            using var conn = new NpgsqlConnection(_conn);
            conn.Open();

            try
            {
                var cmd = new NpgsqlCommand(@"
SELECT t.id, t.subdomain, v.id, v.code, v.expires_at, v.grades_json
FROM tenants t
JOIN tenant_verifications v ON v.tenant_id = t.id
WHERE t.email = @email;", conn);

                cmd.Parameters.AddWithValue("email", email);

                using var reader = cmd.ExecuteReader();
                if (!reader.Read())
                {
                    Console.WriteLine($"[VERIFY] Tenant not found for email: {email}");
                    return Content("Tenant not found.");
                }

                int tenantId = reader.GetInt32(0);
                string subdomain = reader.GetString(1);
                int verificationId = reader.GetInt32(2);
                string storedCode = reader.GetString(3);
                DateTime expiresAt = reader.GetDateTime(4);
                string gradesJson = reader.IsDBNull(5) ? null : reader.GetString(5);

                Console.WriteLine($"[VERIFY] Tenant found: ID={tenantId}, Subdomain={subdomain}");
                Console.WriteLine($"[VERIFY] Stored code: {storedCode}, Expires at: {expiresAt}");
                Console.WriteLine($"[VERIFY] Grades JSON from DB: {(gradesJson == null ? "NULL" : gradesJson)}");
                Console.WriteLine($"[VERIFY] Grades JSON length: {gradesJson?.Length ?? 0} characters");

                reader.Close();

                if (!string.Equals(code.Trim(), storedCode.Trim(), StringComparison.Ordinal))
                {
                    Console.WriteLine($"[VERIFY] Invalid code. Expected: '{storedCode}', Got: '{code}'");
                    return Content("Invalid verification code.");
                }

                if (DateTime.UtcNow > expiresAt)
                {
                    Console.WriteLine($"[VERIFY] Code expired. Current time: {DateTime.UtcNow}, Expires at: {expiresAt}");
                    return Content("Verification code expired.");
                }

                Console.WriteLine($"[VERIFY] Code is valid. Proceeding with account activation.");

                // Activate tenant
                new NpgsqlCommand(
                    "UPDATE tenants SET verified = true WHERE id = @id",
                    conn)
                { Parameters = { new("id", tenantId) } }
                .ExecuteNonQuery();

                Console.WriteLine($"[VERIFY] Tenant activated");

                // Cleanup verification record
                new NpgsqlCommand(
                    "DELETE FROM tenant_verifications WHERE id = @id",
                    conn)
                { Parameters = { new("id", verificationId) } }
                .ExecuteNonQuery();

                Console.WriteLine($"[VERIFY] Verification record deleted");

                // Create tenant schema
                CreateTenantSchema(conn, subdomain);

                // Seed grades from verification record
                if (string.IsNullOrEmpty(gradesJson))
                {
                    Console.WriteLine($"[VERIFY] No grades JSON found. Skipping grade seeding.");
                }
                else
                {
                    Console.WriteLine($"[VERIFY] Attempting to deserialize grades JSON...");
                    try
                    {
                        var grades = JsonSerializer.Deserialize<List<GradeSeedDto>>(gradesJson);
                        Console.WriteLine($"[VERIFY] Deserialization successful. Grades count: {grades?.Count ?? 0}");

                        if (grades != null)
                        {
                            foreach (var g in grades)
                            {
                                Console.WriteLine($"  [VERIFY] Grade from JSON: Name='{g.Name}', Term1={g.Fees?.Term1}, Streams={string.Join(",", g.Streams ?? new())}");
                            }
                        }

                        if (grades != null && grades.Count > 0)
                        {
                            Console.WriteLine($"[VERIFY] Starting grade seeding for schema: tenant_{subdomain}");
                            SeedGrades(conn, $"tenant_{subdomain}", grades);
                            Console.WriteLine($"[VERIFY] Grade seeding completed");
                        }
                        else
                        {
                            Console.WriteLine($"[VERIFY] Grades list is null or empty after deserialization");
                        }
                    }
                    catch (JsonException jex)
                    {
                        Console.WriteLine($"[VERIFY] JSON deserialization error: {jex.Message}");
                        Console.WriteLine($"[VERIFY] JSON content: {gradesJson}");
                        throw;
                    }
                }

                Console.WriteLine($"[VERIFY] Verification complete. Redirecting to login.");
                return Redirect($"http://localhost:5201/{subdomain}/login");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VERIFY ERROR] {ex.Message}");
                Console.WriteLine($"[VERIFY ERROR STACKTRACE] {ex.StackTrace}");
                return Content($"Verification failed: {ex.Message}");
            }
        }
        [HttpGet("/api/schools")]
public IActionResult GetSchools()
{
    using var conn = new NpgsqlConnection(_conn);
    conn.Open();
    
    var cmd = new NpgsqlCommand(
        "SELECT id, name, subdomain FROM tenants WHERE verified = true ORDER BY name",
        conn
    );
    
    var schools = new List<dynamic>();
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
        schools.Add(new { 
            id = reader.GetInt32(0),
            name = reader.GetString(1),
            subdomain = reader.GetString(2)
        });
    }
    
    return Json(schools);
}

        // =========================
        // CREATE TENANT SCHEMA
        // =========================
private void CreateTenantSchema(NpgsqlConnection conn, string subdomain)
{
    Console.WriteLine($"[SCHEMA] Creating schema for subdomain: {subdomain}");
    string schema = $"tenant_{subdomain}";

    string sql = $@"
CREATE SCHEMA IF NOT EXISTS ""{schema}"";

-- Students Table
CREATE TABLE IF NOT EXISTS ""{schema}"".""Students"" (
    id SERIAL PRIMARY KEY,
    account_no VARCHAR(50) UNIQUE NOT NULL,
    full_name TEXT NOT NULL,
    date_of_birth DATE NOT NULL,
    gender VARCHAR(10),
    grade VARCHAR(50) NOT NULL,
    stream VARCHAR(50),
    admission_date DATE NOT NULL,
    previous_school TEXT,
    photo_path TEXT,
    medical_info TEXT,
    status VARCHAR(30) DEFAULT 'Active',
    created_at TIMESTAMP DEFAULT NOW()
);

-- Payments Table
CREATE TABLE IF NOT EXISTS ""{schema}"".""Payments"" (
    id SERIAL PRIMARY KEY,
    student_id INT NOT NULL REFERENCES ""{schema}"".""Students""(id) ON DELETE CASCADE,
    amount INT NOT NULL,
    phone VARCHAR(20) NOT NULL,
    payment_method VARCHAR(50) DEFAULT 'MPesa',
    status VARCHAR(30) DEFAULT 'Pending',
    transaction_id VARCHAR(100) UNIQUE,
    reference VARCHAR(100),
    created_at TIMESTAMP DEFAULT NOW(),
    completed_at TIMESTAMP,
    mpesa_receipt VARCHAR(100)
);

-- Secretaries Table (password_hash is now nullable)
CREATE TABLE IF NOT EXISTS ""{schema}"".""Secretaries"" (
    id SERIAL PRIMARY KEY,
    full_name VARCHAR(200) NOT NULL,
    email VARCHAR(255) UNIQUE NOT NULL,
    password_hash TEXT,
    is_active BOOLEAN DEFAULT TRUE,
    phone VARCHAR(20),
    created_at TIMESTAMP DEFAULT NOW(),
    updated_at TIMESTAMP DEFAULT NOW(),
    last_login TIMESTAMP,
    created_by_user VARCHAR(255)
);

CREATE INDEX IF NOT EXISTS idx_secretaries_email 
    ON ""{schema}"".""Secretaries"" (email);

-- StaffCredentials Table (NEW)
CREATE TABLE IF NOT EXISTS ""{schema}"".""StaffCredentials"" (
    id SERIAL PRIMARY KEY,
    secretary_id INTEGER NOT NULL REFERENCES ""{schema}"".""Secretaries""(id) ON DELETE CASCADE,
    username VARCHAR(100) UNIQUE NOT NULL,
    password_hash TEXT NOT NULL,
    role VARCHAR(50) DEFAULT 'staff',
    position VARCHAR(100),
    department VARCHAR(100),
    must_change_password BOOLEAN DEFAULT TRUE,
    is_active BOOLEAN DEFAULT TRUE,
    created_at TIMESTAMP DEFAULT NOW(),
    updated_at TIMESTAMP DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_staff_credentials_username 
    ON ""{schema}"".""StaffCredentials"" (username);

CREATE INDEX IF NOT EXISTS idx_staff_credentials_secretary 
    ON ""{schema}"".""StaffCredentials"" (secretary_id);

-- Guardians Table
CREATE TABLE IF NOT EXISTS ""{schema}"".""Guardians"" (
    id SERIAL PRIMARY KEY,
    student_id INT NOT NULL REFERENCES ""{schema}"".""Students""(id) ON DELETE CASCADE,
    full_name TEXT NOT NULL,
    relationship VARCHAR(30) NOT NULL,
    phone VARCHAR(20) NOT NULL,
    email TEXT,
    address TEXT,
    is_primary BOOLEAN DEFAULT TRUE,
    created_at TIMESTAMP DEFAULT NOW()
);

-- Grades Table
CREATE TABLE IF NOT EXISTS ""{schema}"".""Grades"" (
    id SERIAL PRIMARY KEY,
    grade_name VARCHAR(50) UNIQUE NOT NULL,
    term1_fee INT NOT NULL,
    term2_fee INT NOT NULL,
    term3_fee INT NOT NULL,
    streams TEXT NOT NULL,
    created_at TIMESTAMP DEFAULT NOW()
);

-- Streams Table
CREATE TABLE IF NOT EXISTS ""{schema}"".""Streams"" (
    id SERIAL PRIMARY KEY,
    grade_name VARCHAR(50) NOT NULL,
    stream_name VARCHAR(50) NOT NULL,
    created_at TIMESTAMP DEFAULT NOW(),
    CONSTRAINT uq_grade_stream UNIQUE (grade_name, stream_name)
);
-- Notifications Table (NEW)
CREATE TABLE IF NOT EXISTS ""{schema}"".""Notifications"" (
    id SERIAL PRIMARY KEY,
    student_id INT NOT NULL REFERENCES ""{schema}"".""Students""(id) ON DELETE CASCADE,
    title TEXT NOT NULL,
    message TEXT NOT NULL,
    type VARCHAR(50) DEFAULT 'general',
    is_read BOOLEAN DEFAULT FALSE,
    created_at TIMESTAMP DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_notifications_student
    ON ""{schema}"".""Notifications"" (student_id);

CREATE INDEX IF NOT EXISTS idx_notifications_is_read
    ON ""{schema}"".""Notifications"" (is_read);

-- SchoolInfo Table (NEW)
CREATE TABLE IF NOT EXISTS ""{schema}"".""SchoolInfo"" (
    id SERIAL PRIMARY KEY,
    school_name VARCHAR(255),
    registration_number VARCHAR(100),
    established_year INTEGER,
    school_motto TEXT,
    badge_url TEXT,
    created_at TIMESTAMP DEFAULT NOW(),
    updated_at TIMESTAMP DEFAULT NOW()
);

-- SchoolContact Table (NEW)
CREATE TABLE IF NOT EXISTS ""{schema}"".""SchoolContact"" (
    id SERIAL PRIMARY KEY,
    email VARCHAR(255),
    phone VARCHAR(50),
    address TEXT,
    website VARCHAR(255),
    created_at TIMESTAMP DEFAULT NOW(),
    updated_at TIMESTAMP DEFAULT NOW()
);

-- Insert default school info
INSERT INTO ""{schema}"".""SchoolInfo"" (school_name, school_motto, created_at)
VALUES ('{subdomain}', 'Excellence in Education', NOW())
ON CONFLICT DO NOTHING;

-- Insert default contact info
INSERT INTO ""{schema}"".""SchoolContact"" (created_at)
VALUES (NOW())
ON CONFLICT DO NOTHING;

-- Indexes
CREATE INDEX IF NOT EXISTS idx_students_grade
    ON ""{schema}"".""Students"" (grade);

CREATE INDEX IF NOT EXISTS idx_students_status
    ON ""{schema}"".""Students"" (status);

CREATE INDEX IF NOT EXISTS idx_guardians_phone
    ON ""{schema}"".""Guardians"" (phone);

CREATE INDEX IF NOT EXISTS idx_payments_student
    ON ""{schema}"".""Payments"" (student_id);

CREATE INDEX IF NOT EXISTS idx_payments_status
    ON ""{schema}"".""Payments"" (status);
";

    using var cmd = new NpgsqlCommand(sql, conn);
    cmd.ExecuteNonQuery();

    Console.WriteLine($"[SCHEMA] Schema created successfully: {schema}");
}
        // =========================
        // SEED GRADES
        // =========================
        private void SeedGrades(NpgsqlConnection conn, string schema, List<GradeSeedDto> grades)
        {
            Console.WriteLine($"[SEED] SeedGrades called with schema: {schema}, grades count: {grades?.Count ?? 0}");

            if (grades == null || grades.Count == 0)
            {
                Console.WriteLine($"[SEED] ERROR: No grades provided for seeding");
                throw new Exception("No grades provided for seeding.");
            }

            var checkCmd = new NpgsqlCommand(
                $@"SELECT COUNT(*) FROM ""{schema}"".""Grades"";",
                conn
            );

            long existingCount = (long)checkCmd.ExecuteScalar();
            Console.WriteLine($"[SEED] Existing grades in {schema}: {existingCount}");

            if (existingCount > 0)
            {
                Console.WriteLine($"[SEED] Grades already exist for {schema}. Skipping seeding.");
                return;
            }

            foreach (var grade in grades)
            {
                Console.WriteLine($"[SEED] Processing grade: Name='{grade.Name}', Term1={grade.Fees?.Term1}, Streams={string.Join(",", grade.Streams ?? new())}");

                // 1. Validate
                if (string.IsNullOrWhiteSpace(grade.Name))
                {
                    Console.WriteLine($"[SEED] ERROR: Grade name is null or whitespace!");
                    throw new Exception("Grade name cannot be empty during seeding.");
                }

                Console.WriteLine($"[SEED] Grade name validated: '{grade.Name}'");

                if (grade.Fees == null)
                {
                    Console.WriteLine($"[SEED] ERROR: Fees object is null for grade '{grade.Name}'!");
                    throw new Exception($"Fees cannot be null for grade '{grade.Name}'");
                }

                // Convert streams array to CSV string
                string streamsCsv = (grade.Streams != null && grade.Streams.Count > 0)
                    ? string.Join(",", grade.Streams)
                    : "NON";

                Console.WriteLine($"[SEED] Streams array: [{string.Join(", ", grade.Streams ?? new())}]");
                Console.WriteLine($"[SEED] Streams CSV to be stored: '{streamsCsv}'");

                // 2. Build command
                using var insertCmd = new NpgsqlCommand($@"
INSERT INTO ""{schema}"".""Grades""
(grade_name, term1_fee, term2_fee, term3_fee, streams)
VALUES
(@gradeName, @term1, @term2, @term3, @streams);
", conn);

                // 3. Bind parameters
                insertCmd.Parameters.Add("gradeName", NpgsqlTypes.NpgsqlDbType.Varchar)
                    .Value = grade.Name;

                insertCmd.Parameters.Add("term1", NpgsqlTypes.NpgsqlDbType.Integer)
                    .Value = grade.Fees.Term1;

                insertCmd.Parameters.Add("term2", NpgsqlTypes.NpgsqlDbType.Integer)
                    .Value = grade.Fees.Term2;

                insertCmd.Parameters.Add("term3", NpgsqlTypes.NpgsqlDbType.Integer)
                    .Value = grade.Fees.Term3;

                insertCmd.Parameters.Add("streams", NpgsqlTypes.NpgsqlDbType.Text)
                    .Value = streamsCsv;

                Console.WriteLine($"[SEED] Inserting grade: {grade.Name} with fees T1:{grade.Fees.Term1}, T2:{grade.Fees.Term2}, T3:{grade.Fees.Term3}, Streams:{streamsCsv}");

                // 4. Execute
                insertCmd.ExecuteNonQuery();
                Console.WriteLine($"[SEED] Grade '{grade.Name}' inserted successfully");
            }

            Console.WriteLine($"[SEED] Completed seeding {grades.Count} grades for {schema}");
        }

        // =========================
        // HELPERS
        // =========================
        private static string SanitizeSubdomain(string schoolName)
        {
            if (string.IsNullOrWhiteSpace(schoolName))
                throw new ArgumentException("School name cannot be empty");

            return schoolName
                .Trim()
                .ToLowerInvariant()
                .Replace(" ", "")
                .Replace(".", "")
                .Replace("-", "")
                .Replace("_", "");
        }

        private bool SubdomainExists(string subdomain, NpgsqlConnection conn)
        {
            using var cmd = new NpgsqlCommand(
                "SELECT 1 FROM tenants WHERE subdomain = @s",
                conn);
            cmd.Parameters.AddWithValue("s", subdomain);
            return cmd.ExecuteScalar() != null;
        }

        private static string GenerateCode() =>
            Random.Shared.Next(100000, 999999).ToString();

        private bool SmtpConfigured() =>
            !string.IsNullOrWhiteSpace(_config["Smtp:User"]) &&
            !string.IsNullOrWhiteSpace(_config["Smtp:Pass"]);

        private void SendVerificationEmail(
            string to,
            string schoolName,
            string code,
            string subdomain,
            decimal planAmount)
        {
            try
            {
                var smtpUser = _config["Smtp:User"];
                var smtpPass = _config["Smtp:Pass"];

                using var smtp = new SmtpClient("smtp.gmail.com", 587)
                {
                    Credentials = new NetworkCredential(smtpUser, smtpPass),
                    EnableSsl = true
                };

                var verifyLink =
                    $"http://localhost:5201/subscribe/verify" +
                    $"?email={Uri.EscapeDataString(to)}&code={Uri.EscapeDataString(code)}";

                var message = new MailMessage
                {
                    From = new MailAddress(smtpUser, "EduTrack"),
                    Subject = "Verify Your EduTrack Account",
                    IsBodyHtml = true,
                    Body = $@"
<!DOCTYPE html>
<html lang='en'>
<head>
<meta charset='UTF-8'>
<meta name='viewport' content='width=device-width, initial-scale=1.0'>
<title>EduTrack Verification</title>
<style>
body, html {{
    margin:0;
    padding:0;
    font-family:'Segoe UI',sans-serif;
    background:#f4f4f4;
}}
.container {{
    max-width:600px;
    margin:30px auto;
    background:#fff;
    border-radius:10px;
    box-shadow:0 4px 20px rgba(0,0,0,0.1);
}}
.header {{
    background:#2E86C1;
    color:#fff;
    padding:25px;
    text-align:center;
}}
.header h1 {{
    margin:0;
    font-size:22px;
}}
.content {{
    padding:25px;
    color:#333;
    font-size:14px;
    line-height:1.6;
}}
.code {{
    font-size:24px;
    font-weight:bold;
    color:#2E86C1;
    text-align:center;
    margin:20px 0;
    letter-spacing:2px;
}}
.btn {{
    display:inline-block;
    background:#2E86C1;
    color:#fff;
    padding:12px 25px;
    border-radius:6px;
    font-weight:bold;
    text-decoration:none;
}}
.btn:hover {{
    background:#1B4F72;
}}
.footer {{
    font-size:12px;
    color:#888;
    text-align:center;
    margin-top:30px;
}}
@media only screen and (max-width:480px) {{
    .content {{ padding:20px; font-size:13px; }}
    .header h1 {{ font-size:20px; }}
    .code {{ font-size:22px; }}
    .btn {{ padding:10px 18px; font-size:14px; }}
}}
</style>
</head>
<body>
<div class='container'>
    <div class='header'>
        <h1>EduTrack Account Verification</h1>
    </div>
    <div class='content'>
        <p>Hello <strong>{schoolName}</strong>,</p>

        <p>Please use the verification code below to activate your account:</p>

        <div class='code'>{code}</div>

        <p>This code expires in <strong>10 minutes</strong>.</p>

        <hr/>

        <p><strong>School Subdomain:</strong> {subdomain}</p>
        <p><strong>Plan Amount:</strong> KES {planAmount:N0}</p>

        <p style='text-align:center; margin-top:20px;'>
            <a href='{verifyLink}' class='btn'>Verify My Account</a>
        </p>

        <p>If you did not initiate this request, you may safely ignore this email.</p>

        <div class='footer'>
            EduTrack Team &copy; 2025
        </div>
    </div>
</div>
</body>
</html>"
                };

                message.To.Add(to);
                smtp.Send(message);

                Console.WriteLine($"[EMAIL] Verification email sent to {to}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EMAIL ERROR] {ex.Message}");
            }
        }
    }

    // =========================
    // DTOs
    // =========================

    public class GradeSeedDto
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("fees")]
        public FeeDto Fees { get; set; }

        [JsonPropertyName("streams")]
        public List<string> Streams { get; set; } = new();
    }

    public class SubscriptionRequest
    {
        [Required(ErrorMessage = "School name is required")]
        public string SchoolName { get; set; }

        [Required(ErrorMessage = "Admin email is required")]
        [EmailAddress]
        public string AdminEmail { get; set; }

        [Required(ErrorMessage = "Password is required")]
        public string Password { get; set; }

        [Required(ErrorMessage = "School gender is required")]
        public string SchoolGender { get; set; }

        [Range(0.01, double.MaxValue, ErrorMessage = "Plan amount must be greater than 0")]
        public decimal PlanAmount { get; set; }

        [Required(ErrorMessage = "At least one grade is required")]
        public List<GradeSeedDto> Grades { get; set; } = new List<GradeSeedDto>();
    }
}