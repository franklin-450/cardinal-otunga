using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Npgsql;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Linq;

namespace EduTrackTrial.Controllers
{
    [Route("{schoolName}/principal")]
    public class PrincipalController(IConfiguration config, ILogger<PrincipalController> logger) : Controller
    {
        private readonly string? _conn = config.GetConnectionString("DefaultConnection");
        private readonly ILogger<PrincipalController> _logger = logger;
        private readonly IConfiguration _config = config;

        // =========================
        // AUTHORIZATION CHECK
        // =========================
        private bool IsPrincipalAuthorized(string schoolName)
        {
            var tenant = HttpContext.Session.GetString("tenant");
            var user = HttpContext.Session.GetString("user");
            
            return !string.IsNullOrEmpty(tenant) &&
                   tenant.Equals(schoolName, StringComparison.OrdinalIgnoreCase) &&
                   !string.IsNullOrEmpty(user);
        }

        // =========================
        // PRINCIPAL DASHBOARD
        // =========================
        [HttpGet("")]
        [HttpGet("index")]
        public async Task<IActionResult> Index(string schoolName)
        {
            if (!IsPrincipalAuthorized(schoolName))
            {
                _logger.LogWarning("Unauthorized principal access for {SchoolName}", schoolName);
                return Redirect($"/{schoolName}/login");
            }

            try
            {
                var (tenantId, schema) = await ResolveTenantAsync(schoolName);

                // Fetch school configuration from database
                var schoolConfig = await GetSchoolConfigAsync(tenantId, schema);

                ViewData["SchoolName"] = schoolConfig?.SchoolName ?? schoolName;
                ViewData["UserEmail"] = HttpContext.Session.GetString("user");
                ViewData["SchoolLogo"] = schoolConfig?.BadgeUrl ?? "";
                ViewData["SchoolMotto"] = schoolConfig?.SchoolMotto ?? "Excellence in Education";
                
                return View("Dashboard");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading principal dashboard for {SchoolName}", schoolName);
                return Redirect($"/{schoolName}/login");
            }
        }

        // =========================
        // GET SCHOOL CONFIGURATION
        // =========================
        [HttpGet("api/school-config")]
        public async Task<IActionResult> GetSchoolConfig(string schoolName)
        {
            if (!IsPrincipalAuthorized(schoolName))
            {
                return Json(new { success = false, message = "Unauthorized" });
            }

            try
            {
                var (tenantId, schema) = await ResolveTenantAsync(schoolName);
                var config = await GetSchoolConfigAsync(tenantId, schema);

                if (config == null)
                {
                    return Json(new { success = false, message = "School configuration not found" });
                }

                return Json(new { success = true, data = config });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting school config for {SchoolName}", schoolName);
                return Json(new { success = false, message = "Error loading configuration" });
            }
        }

        // =========================
        // GET DASHBOARD STATISTICS
        // =========================
        [HttpGet("api/dashboard-stats")]
        public async Task<IActionResult> GetDashboardStats(string schoolName)
        {
            if (!IsPrincipalAuthorized(schoolName))
            {
                return Json(new { success = false, message = "Unauthorized" });
            }

            try
            {
                var (tenantId, schema) = await ResolveTenantAsync(schoolName);

                await using var conn = new NpgsqlConnection(_conn);
                await conn.OpenAsync();

                var stats = new
                {
                    totalLearners = await GetTotalStudentsAsync(conn, schema),
                    totalStaff = await GetTotalStaffAsync(conn, schema),
                    totalRevenue = await GetTotalRevenueAsync(conn, schema),
                    outstandingFees = await GetOutstandingFeesAsync(conn, schema),
                    recentRegistrations = await GetRecentRegistrationsAsync(conn, schema),
                    genderDistribution = await GetGenderDistributionAsync(conn, schema),
                    gradeDistribution = await GetGradeDistributionAsync(conn, schema)
                };

                return Json(new { success = true, data = stats });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting dashboard stats for {SchoolName}", schoolName);
                return Json(new { success = false, message = "Error loading statistics" });
            }
        }

        // =========================
        // GET RECENT ADMISSIONS
        // =========================
        [HttpGet("api/recent-admissions")]
        public async Task<IActionResult> GetRecentAdmissions(string schoolName)
        {
            if (!IsPrincipalAuthorized(schoolName))
            {
                return Json(new { success = false, message = "Unauthorized" });
            }

            try
            {
                var (tenantId, schema) = await ResolveTenantAsync(schoolName);

                await using var conn = new NpgsqlConnection(_conn);
                await conn.OpenAsync();

                var admissions = await GetRecentRegistrationsAsync(conn, schema);

                return Json(new { success = true, admissions });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting recent admissions for {SchoolName}", schoolName);
                return Json(new { success = false, message = "Error loading admissions" });
            }
        }

        // =========================
        // GET ALL LEARNERS
        // =========================
        [HttpGet("api/learners")]
        public async Task<IActionResult> GetLearners(string schoolName)
        {
            if (!IsPrincipalAuthorized(schoolName))
            {
                return Json(new { success = false, message = "Unauthorized" });
            }

            try
            {
                var (tenantId, schema) = await ResolveTenantAsync(schoolName);

                await using var conn = new NpgsqlConnection(_conn);
                await conn.OpenAsync();

                var learners = new List<dynamic>();

                await using (var cmd = new NpgsqlCommand($@"
                    SELECT 
                        id,
                        full_name,
                        account_no,
                        grade,
                        stream,
                        status,
                        admission_date,
                        gender,
                        date_of_birth
                    FROM ""{schema}"".""Students""
                    ORDER BY admission_date DESC", conn))
                {
                    await using var reader = await cmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        learners.Add(new
                        {
                            id = reader.GetInt32(0),
                            fullName = reader.GetString(1),
                            admissionNo = reader.GetString(2),
                            @class = reader.GetString(3),
                            stream = reader.IsDBNull(4) ? "General" : reader.GetString(4),
                            status = reader.GetString(5),
                            feesStatus = "Pending", // Calculate from payments table if needed
                            admissionDate = reader.GetDateTime(6),
                            gender = reader.IsDBNull(7) ? "N/A" : reader.GetString(7),
                            dateOfBirth = reader.IsDBNull(8) ? (DateTime?)null : reader.GetDateTime(8)
                        });
                    }
                }

                return Json(new { success = true, learners });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting learners for {SchoolName}", schoolName);
                return Json(new { success = false, message = "Error loading learners" });
            }
        }

        // =========================
        // ENROLL NEW LEARNER
        // =========================
        [HttpPost("api/learners/enroll")]
        public async Task<IActionResult> EnrollLearner(string schoolName, [FromBody] EnrollLearnerDto dto)
        {
            if (!IsPrincipalAuthorized(schoolName))
            {
                return Json(new { success = false, message = "Unauthorized" });
            }

            if (string.IsNullOrWhiteSpace(dto?.FullName) || string.IsNullOrWhiteSpace(dto?.Class))
            {
                return Json(new { success = false, message = "Required fields missing" });
            }

            try
            {
                var (tenantId, schema) = await ResolveTenantAsync(schoolName);

                await using var conn = new NpgsqlConnection(_conn);
                await conn.OpenAsync();

                // Generate admission number
                string admissionNo = await GenerateAccountNoAsync(conn, schema);

                await using var cmd = new NpgsqlCommand($@"
                    INSERT INTO ""{schema}"".""Students""
                    (full_name, account_no, grade, stream, status, admission_date, created_at, date_of_birth, gender)
                    VALUES (@fullName, @accountNo, @grade, @stream, 'Active', NOW(), NOW(), @dob, @gender)
                    RETURNING id", conn);

                cmd.Parameters.AddWithValue("@fullName", dto.FullName);
                cmd.Parameters.AddWithValue("@accountNo", admissionNo);
                cmd.Parameters.AddWithValue("@grade", dto.Class);
                cmd.Parameters.AddWithValue("@stream", dto.Stream ?? "General");
                cmd.Parameters.AddWithValue("@dob", dto.DateOfBirth != null ? (object)DateTime.Parse(dto.DateOfBirth) : DBNull.Value);
                cmd.Parameters.AddWithValue("@gender", dto.Gender ?? "N/A");

                var studentId = await cmd.ExecuteScalarAsync();

                _logger.LogInformation("Student {StudentId} enrolled in {SchoolName}", studentId, schoolName);

                return Json(new
                {
                    success = true,
                    message = "Student enrolled successfully",
                    studentId = studentId,
                    admissionNo = admissionNo
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error enrolling learner for {SchoolName}", schoolName);
                return Json(new { success = false, message = "Error enrolling student" });
            }
        }

        // =========================
        // GET ALL STAFF
        // =========================
        [HttpGet("api/staff")]
        public async Task<IActionResult> GetStaff(string schoolName)
        {
            if (!IsPrincipalAuthorized(schoolName))
            {
                return Json(new { success = false, message = "Unauthorized" });
            }

            try
            {
                var (tenantId, schema) = await ResolveTenantAsync(schoolName);

                await using var conn = new NpgsqlConnection(_conn);
                await conn.OpenAsync();

                var staff = new List<dynamic>();

                await using (var cmd = new NpgsqlCommand($@"
                    SELECT 
                        s.id,
                        s.full_name,
                        s.email,
                        s.phone,
                        s.is_active,
                        COALESCE(sc.position, 'Staff Member') as position,
                        s.created_at
                    FROM ""{schema}"".""Secretaries"" s
                    LEFT JOIN ""{schema}"".""StaffCredentials"" sc ON s.id = sc.secretary_id
                    ORDER BY s.created_at DESC", conn))
                {
                    await using var reader = await cmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        staff.Add(new
                        {
                            id = reader.GetInt32(0),
                            fullName = reader.GetString(1),
                            email = reader.GetString(2),
                            phone = reader.IsDBNull(3) ? "" : reader.GetString(3),
                            isActive = reader.GetBoolean(4),
                            position = reader.IsDBNull(5) ? "Staff Member" : reader.GetString(5),
                            createdAt = reader.GetDateTime(6)
                        });
                    }
                }

                return Json(new { success = true, staff });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting staff for {SchoolName}", schoolName);
                return Json(new { success = false, message = "Error loading staff" });
            }
        }

        // =========================
        // REGISTER NEW STAFF
        // =========================
        [HttpPost("api/staff/register")]
        public async Task<IActionResult> RegisterStaff(string schoolName, [FromBody] RegisterStaffDto dto)
        {
            if (!IsPrincipalAuthorized(schoolName))
            {
                return Json(new { success = false, message = "Unauthorized" });
            }

            if (string.IsNullOrWhiteSpace(dto?.FullName) || string.IsNullOrWhiteSpace(dto?.Email))
            {
                return Json(new { success = false, message = "Required fields missing" });
            }

            try
            {
                var (tenantId, schema) = await ResolveTenantAsync(schoolName);

                await using var conn = new NpgsqlConnection(_conn);
                await conn.OpenAsync();

                await using var cmd = new NpgsqlCommand($@"
                    INSERT INTO ""{schema}"".""Secretaries""
                    (full_name, email, phone, is_active, created_at)
                    VALUES (@fullName, @email, @phone, true, NOW())
                    RETURNING id", conn);

                cmd.Parameters.AddWithValue("@fullName", dto.FullName);
                cmd.Parameters.AddWithValue("@email", dto.Email);
                cmd.Parameters.AddWithValue("@phone", dto.Phone ?? (object)DBNull.Value);

                var staffId = await cmd.ExecuteScalarAsync();

                _logger.LogInformation("Staff member {StaffId} registered in {SchoolName}", staffId, schoolName);

                return Json(new
                {
                    success = true,
                    message = "Staff registered successfully",
                    staffId = staffId
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error registering staff for {SchoolName}", schoolName);
                return Json(new { success = false, message = "Error registering staff" });
            }
        }

        // =========================
        // CREATE STAFF CREDENTIALS
        // =========================
        [HttpPost("api/credentials/create")]
        public async Task<IActionResult> CreateCredentials(string schoolName, [FromBody] CreateCredentialsDto dto)
        {
            if (!IsPrincipalAuthorized(schoolName))
            {
                return Json(new { success = false, message = "Unauthorized" });
            }

            if (string.IsNullOrWhiteSpace(dto?.Username) || string.IsNullOrWhiteSpace(dto?.TempPassword))
            {
                return Json(new { success = false, message = "Username and password required" });
            }

            try
            {
                var (tenantId, schema) = await ResolveTenantAsync(schoolName);

                await using var conn = new NpgsqlConnection(_conn);
                await conn.OpenAsync();

                var hashedPassword = HashPassword(dto.TempPassword);

                await using var cmd = new NpgsqlCommand($@"
                    INSERT INTO ""{schema}"".""StaffCredentials""
                    (secretary_id, username, password_hash, role, position, department, must_change_password, is_active, created_at)
                    VALUES (@secretaryId, @username, @passwordHash, @role, @position, @department, true, true, NOW())
                    ON CONFLICT (username) DO UPDATE SET
                        password_hash = @passwordHash,
                        must_change_password = true,
                        updated_at = NOW()
                    RETURNING id", conn);

                cmd.Parameters.AddWithValue("@secretaryId", dto.StaffId);
                cmd.Parameters.AddWithValue("@username", dto.Username);
                cmd.Parameters.AddWithValue("@passwordHash", hashedPassword);
                cmd.Parameters.AddWithValue("@role", dto.Role ?? "staff");
                cmd.Parameters.AddWithValue("@position", dto.Position ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@department", dto.Department ?? (object)DBNull.Value);

                var credentialId = await cmd.ExecuteScalarAsync();

                _logger.LogInformation("Credentials created for staff {StaffId} in {SchoolName}", dto.StaffId, schoolName);

                return Json(new
                {
                    success = true,
                    message = "Credentials created successfully",
                    credentialId = credentialId
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating credentials for {SchoolName}", schoolName);
                return Json(new { success = false, message = "Error creating credentials" });
            }
        }

        // =========================
        // GET CREDENTIALS HISTORY
        // =========================
        [HttpGet("api/credentials/history")]
        public async Task<IActionResult> GetCredentialsHistory(string schoolName)
        {
            if (!IsPrincipalAuthorized(schoolName))
            {
                return Json(new { success = false, message = "Unauthorized" });
            }

            try
            {
                var (tenantId, schema) = await ResolveTenantAsync(schoolName);

                await using var conn = new NpgsqlConnection(_conn);
                await conn.OpenAsync();

                var credentials = new List<dynamic>();

                await using (var cmd = new NpgsqlCommand($@"
                    SELECT 
                        sc.id,
                        s.full_name,
                        sc.username,
                        sc.role,
                        sc.position,
                        sc.is_active,
                        sc.created_at
                    FROM ""{schema}"".""StaffCredentials"" sc
                    JOIN ""{schema}"".""Secretaries"" s ON sc.secretary_id = s.id
                    ORDER BY sc.created_at DESC
                    LIMIT 20", conn))
                {
                    await using var reader = await cmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        credentials.Add(new
                        {
                            id = reader.GetInt32(0),
                            staffName = reader.GetString(1),
                            username = reader.GetString(2),
                            role = reader.GetString(3),
                            position = reader.IsDBNull(4) ? "" : reader.GetString(4),
                            isActive = reader.GetBoolean(5),
                            createdDate = reader.GetDateTime(6)
                        });
                    }
                }

                return Json(new { success = true, credentials });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting credentials history for {SchoolName}", schoolName);
                return Json(new { success = false, message = "Error loading credentials history" });
            }
        }

        // =========================
        // UPDATE SCHOOL INFORMATION
        // =========================
        [HttpPost("api/school/update-info")]
        public async Task<IActionResult> UpdateSchoolInfo(string schoolName, [FromBody] UpdateSchoolInfoDto dto)
        {
            if (!IsPrincipalAuthorized(schoolName))
            {
                return Json(new { success = false, message = "Unauthorized" });
            }

            try
            {
                var (tenantId, schema) = await ResolveTenantAsync(schoolName);

                await using var conn = new NpgsqlConnection(_conn);
                await conn.OpenAsync();

                await using var cmd = new NpgsqlCommand($@"
                    INSERT INTO ""{schema}"".""SchoolInfo""
                    (school_name, registration_number, established_year, school_motto, updated_at)
                    VALUES (@schoolName, @regNumber, @year, @motto, NOW())
                    ON CONFLICT (id) DO UPDATE SET
                        school_name = @schoolName,
                        registration_number = @regNumber,
                        established_year = @year,
                        school_motto = @motto,
                        updated_at = NOW()", conn);

                cmd.Parameters.AddWithValue("@schoolName", dto.SchoolName ?? schoolName);
                cmd.Parameters.AddWithValue("@regNumber", dto.RegistrationNumber ?? "");
                cmd.Parameters.AddWithValue("@year", dto.EstablishedYear ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@motto", dto.SchoolMotto ?? "");

                await cmd.ExecuteNonQueryAsync();

                _logger.LogInformation("School info updated for {SchoolName}", schoolName);

                return Json(new { success = true, message = "School information updated" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating school info for {SchoolName}", schoolName);
                return Json(new { success = false, message = "Error updating school information" });
            }
        }

        // =========================
        // UPDATE CONTACT INFORMATION
        // =========================
        [HttpPost("api/school/update-contact")]
        public async Task<IActionResult> UpdateContactInfo(string schoolName, [FromBody] UpdateContactInfoDto dto)
        {
            if (!IsPrincipalAuthorized(schoolName))
            {
                return Json(new { success = false, message = "Unauthorized" });
            }

            try
            {
                var (tenantId, schema) = await ResolveTenantAsync(schoolName);

                await using var conn = new NpgsqlConnection(_conn);
                await conn.OpenAsync();

                await using var cmd = new NpgsqlCommand($@"
                    INSERT INTO ""{schema}"".""SchoolContact""
                    (email, phone, address, website, updated_at)
                    VALUES (@email, @phone, @address, @website, NOW())
                    ON CONFLICT (id) DO UPDATE SET
                        email = @email,
                        phone = @phone,
                        address = @address,
                        website = @website,
                        updated_at = NOW()", conn);

                cmd.Parameters.AddWithValue("@email", dto.Email ?? "");
                cmd.Parameters.AddWithValue("@phone", dto.Phone ?? "");
                cmd.Parameters.AddWithValue("@address", dto.Address ?? "");
                cmd.Parameters.AddWithValue("@website", dto.Website ?? "");

                await cmd.ExecuteNonQueryAsync();

                _logger.LogInformation("Contact info updated for {SchoolName}", schoolName);

                return Json(new { success = true, message = "Contact information updated" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating contact info for {SchoolName}", schoolName);
                return Json(new { success = false, message = "Error updating contact information" });
            }
        }

        // =========================
        // UPLOAD SCHOOL BADGE
        // =========================
        [HttpPost("api/school/upload-badge")]
        public async Task<IActionResult> UploadBadge(string schoolName, IFormFile badge)
        {
            if (!IsPrincipalAuthorized(schoolName))
            {
                return Json(new { success = false, message = "Unauthorized" });
            }

            if (badge == null || badge.Length == 0)
            {
                return Json(new { success = false, message = "No file provided" });
            }

            if (badge.Length > 5 * 1024 * 1024)
            {
                return Json(new { success = false, message = "File size exceeds 5MB limit" });
            }

            try
            {
                var uploadPath = Path.Combine("wwwroot", "uploads", "school_badges");
                if (!Directory.Exists(uploadPath))
                {
                    Directory.CreateDirectory(uploadPath);
                }

                var fileName = $"{schoolName}_{DateTime.Now.Ticks}.jpg";
                var filePath = Path.Combine(uploadPath, fileName);

                using (var stream = new FileStream(filePath, FileMode.Create))
                {
                    await badge.CopyToAsync(stream);
                }

                var badgeUrl = $"/uploads/school_badges/{fileName}";

                var (tenantId, schema) = await ResolveTenantAsync(schoolName);
                await using var conn = new NpgsqlConnection(_conn);
                await conn.OpenAsync();

                await using var cmd = new NpgsqlCommand($@"
                    INSERT INTO ""{schema}"".""SchoolInfo""
                    (badge_url, updated_at)
                    VALUES (@badgeUrl, NOW())
                    ON CONFLICT (id) DO UPDATE SET
                        badge_url = @badgeUrl,
                        updated_at = NOW()", conn);

                cmd.Parameters.AddWithValue("@badgeUrl", badgeUrl);
                await cmd.ExecuteNonQueryAsync();

                _logger.LogInformation("School badge uploaded for {SchoolName}", schoolName);

                return Json(new { success = true, message = "Badge uploaded successfully", badgeUrl });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error uploading badge for {SchoolName}", schoolName);
                return Json(new { success = false, message = "Error uploading badge" });
            }
        }

        // =========================
        // GET GRADES CONFIGURATION
        // =========================
        [HttpGet("api/grades")]
        public async Task<IActionResult> GetGrades(string schoolName)
        {
            if (!IsPrincipalAuthorized(schoolName))
            {
                return Json(new { success = false, message = "Unauthorized" });
            }

            try
            {
                var (tenantId, schema) = await ResolveTenantAsync(schoolName);

                await using var conn = new NpgsqlConnection(_conn);
                await conn.OpenAsync();

                var grades = new List<dynamic>();

                await using (var cmd = new NpgsqlCommand($@"
                    SELECT 
                        grade_name,
                        term1_fee,
                        term2_fee,
                        term3_fee,
                        streams,
                        created_at
                    FROM ""{schema}"".""Grades""
                    ORDER BY grade_name", conn))
                {
                    await using var reader = await cmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        var streamsList = new List<string>();
                        string streamsStr = reader.IsDBNull(4) ? "" : reader.GetString(4);
                        
                        if (!string.IsNullOrWhiteSpace(streamsStr) && streamsStr != "NON")
                        {
                            streamsList = streamsStr.Split(',', System.StringSplitOptions.TrimEntries)
                                .Where(s => !string.IsNullOrWhiteSpace(s))
                                .ToList();
                        }

                        int totalFees = reader.GetInt32(1) + reader.GetInt32(2) + reader.GetInt32(3);

                        grades.Add(new
                        {
                            gradeName = reader.GetString(0),
                            term1Fee = reader.GetInt32(1),
                            term2Fee = reader.GetInt32(2),
                            term3Fee = reader.GetInt32(3),
                            totalFees = totalFees,
                            streams = streamsList,
                            createdAt = reader.GetDateTime(5)
                        });
                    }
                }

                return Json(new { success = true, grades });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting grades for {SchoolName}", schoolName);
                return Json(new { success = false, message = "Error loading grades" });
            }
        }

        // =========================
        // GET LEARNERS BY GRADE
        // =========================
        [HttpGet("api/learners/by-grade/{grade}")]
        public async Task<IActionResult> GetLearnersByGrade(string schoolName, string grade)
        {
            if (!IsPrincipalAuthorized(schoolName))
            {
                return Json(new { success = false, message = "Unauthorized" });
            }

            try
            {
                var (tenantId, schema) = await ResolveTenantAsync(schoolName);

                await using var conn = new NpgsqlConnection(_conn);
                await conn.OpenAsync();

                var learners = new List<dynamic>();

                await using (var cmd = new NpgsqlCommand($@"
                    SELECT 
                        id,
                        full_name,
                        account_no,
                        grade,
                        stream,
                        status,
                        admission_date,
                        gender,
                        date_of_birth
                    FROM ""{schema}"".""Students""
                    WHERE grade = @grade AND status = 'Active'
                    ORDER BY full_name", conn))
                {
                    cmd.Parameters.AddWithValue("@grade", grade);
                    
                    await using var reader = await cmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        learners.Add(new
                        {
                            id = reader.GetInt32(0),
                            fullName = reader.GetString(1),
                            admissionNo = reader.GetString(2),
                            @class = reader.GetString(3),
                            stream = reader.IsDBNull(4) ? "General" : reader.GetString(4),
                            status = reader.GetString(5),
                            admissionDate = reader.GetDateTime(6),
                            gender = reader.IsDBNull(7) ? "N/A" : reader.GetString(7),
                            dateOfBirth = reader.IsDBNull(8) ? (DateTime?)null : reader.GetDateTime(8)
                        });
                    }
                }

                return Json(new { success = true, learners });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting learners for grade {Grade} in {SchoolName}", grade, schoolName);
                return Json(new { success = false, message = "Error loading learners" });
            }
        }
        [HttpGet("api/finances")]
        public async Task<IActionResult> GetFinances(string schoolName)
        {
            if (!IsPrincipalAuthorized(schoolName))
            {
                return Json(new { success = false, message = "Unauthorized" });
            }

            try
            {
                var (tenantId, schema) = await ResolveTenantAsync(schoolName);

                await using var conn = new NpgsqlConnection(_conn);
                await conn.OpenAsync();

                var data = new
                {
                    totalRevenue = await GetTotalRevenueAsync(conn, schema),
                    outstandingFees = await GetOutstandingFeesAsync(conn, schema),
                    collectionRate = await GetCollectionRateAsync(conn, schema)
                };

                return Json(new { success = true, data });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting finances for {SchoolName}", schoolName);
                return Json(new { success = false, message = "Error loading finances" });
            }
        }

        // =========================
        // HELPER METHODS
        // =========================

        private async Task<(int tenantId, string schema)> ResolveTenantAsync(string school)
        {
            await using var conn = new NpgsqlConnection(_conn);
            await conn.OpenAsync();

            await using var cmd = new NpgsqlCommand(
                "SELECT id, subdomain FROM tenants WHERE LOWER(subdomain)=@s AND verified=true", conn);
            cmd.Parameters.AddWithValue("@s", school.ToLower());

            await using var r = await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync())
                throw new Exception("Tenant not found");

            return (r.GetInt32(0), $"tenant_{r.GetString(1)}");
        }

        private async Task<SchoolConfigDto?> GetSchoolConfigAsync(int tenantId, string schema)
        {
            try
            {
                await using var conn = new NpgsqlConnection(_conn);
                await conn.OpenAsync();

                await using var cmd = new NpgsqlCommand($@"
                    SELECT 
                        school_name,
                        registration_number,
                        established_year,
                        school_motto,
                        badge_url
                    FROM ""{schema}"".""SchoolInfo""
                    LIMIT 1", conn);

                await using var reader = await cmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    return new SchoolConfigDto
                    {
                        SchoolName = reader.IsDBNull(0) ? "School" : reader.GetString(0),
                        RegistrationNumber = reader.IsDBNull(1) ? "" : reader.GetString(1),
                        EstablishedYear = reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
                        SchoolMotto = reader.IsDBNull(3) ? "Excellence in Education" : reader.GetString(3),
                        BadgeUrl = reader.IsDBNull(4) ? "" : reader.GetString(4)
                    };
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching school config for schema {Schema}", schema);
                return null;
            }
        }

        private async Task<int> GetTotalStudentsAsync(NpgsqlConnection conn, string schema)
        {
            await using var cmd = new NpgsqlCommand($@"
                SELECT COUNT(*) FROM ""{schema}"".""Students""
                WHERE status = 'Active'", conn);

            var result = await cmd.ExecuteScalarAsync();
            return result == null || result == DBNull.Value ? 0 : Convert.ToInt32(result);
        }

        private async Task<int> GetTotalStaffAsync(NpgsqlConnection conn, string schema)
        {
            await using var cmd = new NpgsqlCommand($@"
                SELECT COUNT(*) FROM ""{schema}"".""Secretaries""
                WHERE is_active = true", conn);

            var result = await cmd.ExecuteScalarAsync();
            return result == null || result == DBNull.Value ? 0 : Convert.ToInt32(result);
        }

        private async Task<decimal> GetTotalRevenueAsync(NpgsqlConnection conn, string schema)
        {
            await using var cmd = new NpgsqlCommand($@"
                SELECT COALESCE(SUM(amount), 0)
                FROM ""{schema}"".""Payments""
                WHERE status = 'Completed'", conn);

            return Convert.ToDecimal(await cmd.ExecuteScalarAsync() ?? 0);
        }

        private async Task<decimal> GetOutstandingFeesAsync(NpgsqlConnection conn, string schema)
        {
            await using var cmd = new NpgsqlCommand($@"
                SELECT 
                    COALESCE(SUM(g.term1_fee + g.term2_fee + g.term3_fee), 0) -
                    COALESCE((SELECT SUM(amount) FROM ""{schema}"".""Payments"" WHERE status = 'Completed'), 0)
                FROM ""{schema}"".""Students"" s
                LEFT JOIN ""{schema}"".""Grades"" g ON s.grade = g.grade_name
                WHERE s.status = 'Active'", conn);

            return Convert.ToDecimal(await cmd.ExecuteScalarAsync() ?? 0);
        }

        private async Task<decimal> GetCollectionRateAsync(NpgsqlConnection conn, string schema)
        {
            await using var cmd = new NpgsqlCommand($@"
                SELECT 
                    CASE 
                        WHEN SUM(g.term1_fee + g.term2_fee + g.term3_fee) = 0 THEN 0
                        ELSE (COALESCE((SELECT SUM(amount) FROM ""{schema}"".""Payments"" WHERE status = 'Completed'), 0) / 
                              SUM(g.term1_fee + g.term2_fee + g.term3_fee) * 100)
                    END
                FROM ""{schema}"".""Students"" s
                LEFT JOIN ""{schema}"".""Grades"" g ON s.grade = g.grade_name
                WHERE s.status = 'Active'", conn);

            var result = await cmd.ExecuteScalarAsync();
            return result == null || result == DBNull.Value ? 0 : Convert.ToDecimal(result);
        }

        private async Task<List<dynamic>> GetRecentRegistrationsAsync(NpgsqlConnection conn, string schema)
        {
            var registrations = new List<dynamic>();

            await using var cmd = new NpgsqlCommand($@"
                SELECT 
                    id,
                    account_no,
                    full_name,
                    grade,
                    admission_date
                FROM ""{schema}"".""Students""
                WHERE status = 'Active'
                ORDER BY admission_date DESC
                LIMIT 5", conn);

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                registrations.Add(new
                {
                    id = reader.GetInt32(0),
                    accountNo = reader.GetString(1),
                    fullName = reader.GetString(2),
                    grade = reader.GetString(3),
                    admissionDate = reader.GetDateTime(4),
                    @class = reader.GetString(3)
                });
            }

            return registrations;
        }

        private async Task<dynamic> GetGenderDistributionAsync(NpgsqlConnection conn, string schema)
        {
            await using var cmd = new NpgsqlCommand($@"
                SELECT 
                    COUNT(CASE WHEN gender = 'Male' THEN 1 END) as male_count,
                    COUNT(CASE WHEN gender = 'Female' THEN 1 END) as female_count
                FROM ""{schema}"".""Students""
                WHERE status = 'Active'", conn);

            await using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return new
                {
                    male = reader.GetInt32(0),
                    female = reader.GetInt32(1)
                };
            }

            return new { male = 0, female = 0 };
        }

        private async Task<List<dynamic>> GetGradeDistributionAsync(NpgsqlConnection conn, string schema)
        {
            var distribution = new List<dynamic>();

            await using var cmd = new NpgsqlCommand($@"
                SELECT 
                    grade,
                    COUNT(*) as count
                FROM ""{schema}"".""Students""
                WHERE status = 'Active'
                GROUP BY grade
                ORDER BY grade", conn);

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                distribution.Add(new
                {
                    grade = reader.GetString(0),
                    count = reader.GetInt32(1)
                });
            }

            return distribution;
        }

        private string HashPassword(string password)
        {
            using (var sha256 = SHA256.Create())
            {
                var hashedBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(password));
                return Convert.ToBase64String(hashedBytes);
            }
        }

        private async Task<string> GenerateAccountNoAsync(NpgsqlConnection conn, string schema)
        {
            bool isUnique = false;
            var random = new Random();
            string accountNo;

            do
            {
                int digits = random.Next(6, 8);
                int number = random.Next((int)Math.Pow(10, digits - 1), (int)Math.Pow(10, digits) - 1);
                accountNo = number.ToString();

                await using var cmd = new NpgsqlCommand(
                    $@"SELECT COUNT(*) FROM ""{schema}"".""Students"" WHERE account_no = @acc", conn);
                cmd.Parameters.AddWithValue("@acc", accountNo);

                int count = Convert.ToInt32(await cmd.ExecuteScalarAsync() ?? 0);
                if (count == 0) isUnique = true;

            } while (!isUnique);

            return accountNo;
        }
    }

// =========================
    // DTOs - Data Transfer Objects
    // =========================

    public class SchoolConfigDto
    {
        public required string SchoolName { get; set; }
        public required string RegistrationNumber { get; set; }
        public int EstablishedYear { get; set; }
        public required string SchoolMotto { get; set; }
        public required string BadgeUrl { get; set; }
    }

    public class EnrollLearnerDto
    {
        public required string FullName { get; set; }
        public string? DateOfBirth { get; set; }
        public string? Class { get; set; }
        public string? Stream { get; set; }
        public string? Gender { get; set; }
        public string? GuardianEmail { get; set; }
    }

    public class RegisterStaffDto
    {
        public required string FullName { get; set; }
        public required string Email { get; set; }
        public string? Phone { get; set; }
        public string? Position { get; set; }
    }

    public class CreateCredentialsDto
    {
        public int StaffId { get; set; }
        public required string Username { get; set; }
        public required string TempPassword { get; set; }
        public string? Role { get; set; }
        public string? Position { get; set; }
        public string? Department { get; set; }
        public bool SendEmail { get; set; } = true;
    }

    public class UpdateSchoolInfoDto
    {
        public string? SchoolName { get; set; }
        public string? RegistrationNumber { get; set; }
        public int? EstablishedYear { get; set; }
        public string? SchoolMotto { get; set; }
    }

    public class UpdateContactInfoDto
    {
        public string? Email { get; set; }
        public string? Phone { get; set; }
        public string? Address { get; set; }
        public string? Website { get; set; }
    }
}