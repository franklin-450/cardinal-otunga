using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Npgsql;
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace EduTrackTrial.Controllers
{
    [Route("{schoolName}/admission")]
    public class AdmissionController(IConfiguration config, ILogger<AdmissionController> logger) : Controller
    {
        private readonly string? _conn = config.GetConnectionString("DefaultConnection");
        private readonly ILogger<AdmissionController> _logger = logger;

        // =========================
        // AUTH - OPTIMIZED
        // =========================
        private bool Authorized(string schoolName)
        {
            var tenant = HttpContext.Session.GetString("tenant");
            return !string.IsNullOrEmpty(tenant) &&
                   tenant.Equals(schoolName, StringComparison.OrdinalIgnoreCase);
        }

        private IActionResult Kick(string schoolName) =>
            Redirect($"/{schoolName}/login");

        // =========================
        // DASHBOARD
        // =========================
        [HttpGet("")]
        [HttpGet("index")]
        public IActionResult Index(string schoolName)
        {
            if (!Authorized(schoolName))
            {
                _logger.LogWarning("Unauthorized access attempt to {SchoolName} dashboard", schoolName);
                return Kick(schoolName);
            }

            ViewData["SchoolName"] = HttpContext.Session.GetString("displayName") ?? schoolName;
            return View("Index");
        }

        // =========================
        // DASHBOARD STATS API
        // =========================
[HttpGet("api/dashboard-stats")]
        public async Task<IActionResult> GetDashboardStats(string schoolName)
        {
            if (!Authorized(schoolName))
            {
                return Json(new { success = false, message = "Unauthorized" });
            }

            try
            {
                var (tenantId, schema) = await ResolveTenantAsync(schoolName);

                await using var conn = new NpgsqlConnection(_conn);
                await conn.OpenAsync();

                // Get total students
                int totalStudents = 0;
                await using (var cmd = new NpgsqlCommand(
                    $@"SELECT COUNT(*) FROM ""{schema}"".""Students"" WHERE status = 'Active'", conn))
                {
                    totalStudents = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                }

                // Get registered today
                int registeredToday = 0;
                await using (var cmd = new NpgsqlCommand(
                    $@"SELECT COUNT(*) FROM ""{schema}"".""Students"" 
                       WHERE DATE(admission_date) = CURRENT_DATE AND status = 'Active'", conn))
                {
                    registeredToday = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                }

                // Get registered yesterday (for percentage calculation)
                int registeredYesterday = 0;
                await using (var cmd = new NpgsqlCommand(
                    $@"SELECT COUNT(*) FROM ""{schema}"".""Students"" 
                       WHERE DATE(admission_date) = CURRENT_DATE - INTERVAL '1 day' AND status = 'Active'", conn))
                {
                    registeredYesterday = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                }

                // Calculate percentage change
                int todayChangePercent = 0;
                if (registeredYesterday > 0)
                {
                    todayChangePercent = ((registeredToday - registeredYesterday) / registeredYesterday) * 100;
                }

                // Get male students
                int maleStudents = 0;
                await using (var cmd = new NpgsqlCommand(
                    $@"SELECT COUNT(*) FROM ""{schema}"".""Students"" 
                       WHERE gender = 'Male' AND status = 'Active'", conn))
                {
                    maleStudents = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                }

                // Get female students
                int femaleStudents = 0;
                await using (var cmd = new NpgsqlCommand(
                    $@"SELECT COUNT(*) FROM ""{schema}"".""Students"" 
                       WHERE gender = 'Female' AND status = 'Active'", conn))
                {
                    femaleStudents = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                }

                // Get pending applications (if you have an applications table)
                int pendingApplications = 0;
                await using (var cmd = new NpgsqlCommand(
                    $@"SELECT COUNT(*) FROM ""{schema}"".""Students"" WHERE status = 'Pending'", conn))
                {
                    pendingApplications = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                }

                // Get approved applications
                int approvedApplications = 0;
                await using (var cmd = new NpgsqlCommand(
                    $@"SELECT COUNT(*) FROM ""{schema}"".""Students"" WHERE status = 'Approved'", conn))
                {
                    approvedApplications = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                }

                // Calculate percentages
                int pendingPercent = totalStudents > 0 ? (pendingApplications * 100) / totalStudents : 0;
                int approvedPercent = totalStudents > 0 ? (approvedApplications * 100) / totalStudents : 0;

                _logger.LogInformation(
                    "Dashboard stats retrieved: Total={Total}, Today={Today}, Male={Male}, Female={Female}, Pending={Pending}, Approved={Approved}",
                    totalStudents, registeredToday, maleStudents, femaleStudents, pendingApplications, approvedApplications);

                return Json(new
                {
                    success = true,
                    totalStudents,
                    registeredToday,
                    todayChangePercent,
                    maleStudents,
                    femaleStudents,
                    pendingApplications,
                    pendingPercent,
                    approvedApplications,
                    approvedPercent
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting dashboard stats for {SchoolName}", schoolName);
                return Json(new { success = false, message = "Error retrieving stats" });
            }
        }

        // =========================
        // ACTIVITY LOGS API
        // =========================
        [HttpGet("api/activity-logs")]
        public async Task<IActionResult> GetActivityLogs(string schoolName)
        {
            if (!Authorized(schoolName))
            {
                return Json(new { success = false, message = "Unauthorized" });
            }

            try
            {
                var (tenantId, schema) = await ResolveTenantAsync(schoolName);

                await using var conn = new NpgsqlConnection(_conn);
                await conn.OpenAsync();

                var logs = new List<ActivityLogDto>();

                // Get recent student registrations (last 50)
                await using (var cmd = new NpgsqlCommand($@"
                    SELECT 
                        id, 
                        account_no, 
                        full_name, 
                        admission_date,
                        status
                    FROM ""{schema}"".""Students""
                    ORDER BY admission_date DESC
                    LIMIT 50;", conn))
                await using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        int studentId = reader.GetInt32(0);
                        string accountNo = reader.GetString(1);
                        string fullName = reader.GetString(2);
                        DateTime admissionDate = reader.GetDateTime(3);
                        string status = reader.GetString(4);

                        // Determine type and description based on status
                        string type = "registration";
                        string title = "New Student Registration";
                        string description = $"{fullName} (Account: {accountNo}) registered";

                        if (status == "Approved")
                        {
                            type = "approval";
                            title = "Student Approved";
                            description = $"{fullName}'s application was approved";
                        }
                        else if (status == "Active")
                        {
                            title = "Student Enrolled";
                            description = $"{fullName} has been enrolled into the system";
                        }

                        logs.Add(new ActivityLogDto
                        {
                            Id = studentId,
                            Type = type,
                            Title = title,
                            Description = description,
                            Timestamp = admissionDate
                        });
                    }
                }

                // Sort by timestamp descending
                logs = logs.OrderByDescending(l => l.Timestamp).ToList();

                _logger.LogInformation("Activity logs retrieved: {Count} logs for {SchoolName}", 
                    logs.Count, schoolName);

                return Json(new
                {
                    success = true,
                    logs = logs
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting activity logs for {SchoolName}", schoolName);
                return Json(new { success = false, message = "Error retrieving logs" });
            }
        }

        // =========================
        // ADMISSION CONFIG (GET) - OPTIMIZED
        // =========================
        [HttpGet("config")]
        [ResponseCache(Duration = 300, VaryByQueryKeys = new[] { "schoolName" })] // Cache for 5 minutes
        public async Task<IActionResult> AdmissionConfig(string schoolName)
        {
            _logger.LogInformation("Config request for {SchoolName}", schoolName);

            if (!Authorized(schoolName))
            {
                _logger.LogWarning("Unauthorized config request for {SchoolName}", schoolName);
                return Json(new AdmissionConfigResponse { Success = false, GenderPolicy = "Mixed" });
            }

            try
            {
                var (tenantId, schema) = await ResolveTenantAsync(schoolName);
                
                var grades = new List<GradeDto>();
                var streams = new HashSet<string>();
                string genderPolicy = "Mixed";

                await using var conn = new NpgsqlConnection(_conn);
                await conn.OpenAsync();

                // Fetch school gender
                await using (var gCmd = new NpgsqlCommand(
                    "SELECT school_gender FROM tenants WHERE id = @id", conn))
                {
                    gCmd.Parameters.AddWithValue("id", tenantId);
                    genderPolicy = (await gCmd.ExecuteScalarAsync())?.ToString() ?? "Mixed";
                }

                // Fetch grades with streams - OPTIMIZED single query
                await using (var gradeCmd = new NpgsqlCommand($@"
                    SELECT grade_name, term1_fee, term2_fee, term3_fee, streams
                    FROM ""{schema}"".""Grades""
                    ORDER BY grade_name;", conn))
                await using (var r = await gradeCmd.ExecuteReaderAsync())
                {
                    while (await r.ReadAsync())
                    {
                        string gradeName = r.GetString(0);
                        int term1 = r.GetInt32(1);
                        int term2 = r.GetInt32(2);
                        int term3 = r.GetInt32(3);
                        string streamsCsv = r.GetString(4);

                        var streamsList = new List<string>();
                        if (!string.IsNullOrWhiteSpace(streamsCsv) && streamsCsv != "NON")
                        {
                            streamsList = streamsCsv
                                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                .ToList();

                            foreach (var stream in streamsList)
                                streams.Add(stream);
                        }

                        grades.Add(new GradeDto
                        {
                            Name = gradeName,
                            Fees = new FeeDto { Term1 = term1, Term2 = term2, Term3 = term3 },
                            Streams = streamsList
                        });
                    }
                }

                if (streams.Count == 0)
                    streams.Add("NON");

                _logger.LogInformation("Config loaded: {GradeCount} grades, {StreamCount} streams for {SchoolName}",
                    grades.Count, streams.Count, schoolName);

                return Json(new AdmissionConfigResponse
                {
                    Success = true,
                    Grades = grades,
                    Streams = streams.ToList(),
                    GenderPolicy = genderPolicy
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Config failed for {SchoolName}", schoolName);
                return Json(new AdmissionConfigResponse { Success = false, GenderPolicy = "Mixed" });
            }
        }

        // =========================
        // STUDENT REGISTRATION PAGE
        // =========================
        [HttpGet("applications")]
        public IActionResult Applications(string schoolName)
        {
            if (!Authorized(schoolName))
            {
                _logger.LogWarning("Unauthorized applications access for {SchoolName}", schoolName);
                return Kick(schoolName);
            }

            ViewData["SchoolName"] = HttpContext.Session.GetString("displayName") ?? schoolName;
            return View("Applications");
        }
        

        // =========================
        // STUDENT REGISTRATION (POST) - OPTIMIZED
        // =========================
        [HttpPost("register")]
        [RequestSizeLimit(6_000_000)] // 6MB limit
        public async Task<IActionResult> RegisterStudent(
            string schoolName,
            [FromForm] StudentRegistrationRequest request)
        {
            _logger.LogInformation("Registration started for {StudentName} at {SchoolName}",
                request?.FullName, schoolName);

            if (!Authorized(schoolName))
            {
                _logger.LogWarning("Unauthorized registration attempt for {SchoolName}", schoolName);
                return Json(new { success = false, message = "Unauthorized" });
            }

            if (request == null)
            {
                _logger.LogWarning("Null registration request for {SchoolName}", schoolName);
                return Json(new { success = false, message = "Invalid data" });
            }

            try
            {
                var (tenantId, schema) = await ResolveTenantAsync(schoolName);

                await using var conn = new NpgsqlConnection(_conn);
                await conn.OpenAsync();

                // Use transaction for data consistency
                await using var transaction = await conn.BeginTransactionAsync();

                try
                {
                    // Generate account number
                    string accountNo = await GenerateAccountNoAsync(tenantId, conn, schema);

                    // Handle photo upload asynchronously
                    string? photoPath = null;
                    if (request.Photo != null && request.Photo.Length > 0)
                    {
                        photoPath = await SavePhotoAsync(request.Photo, schema, accountNo);
                    }

                    // Insert student record
                    await using var cmd = new NpgsqlCommand($@"
                        INSERT INTO ""{schema}"".""Students""
                        (account_no, full_name, date_of_birth, gender, grade, stream,
                         admission_date, previous_school, photo_path, medical_info, status)
                        VALUES
                        (@acc, @name, @dob, @gender, @grade, @stream,
                         NOW(), @prev, @photo, @medical, 'Active')
                        RETURNING id;", conn, transaction);

                    cmd.Parameters.AddWithValue("@acc", accountNo);
                    cmd.Parameters.AddWithValue("@name", request.FullName);
                    cmd.Parameters.AddWithValue("@dob", request.DateOfBirth);
                    cmd.Parameters.AddWithValue("@gender", request.Gender);
                    cmd.Parameters.AddWithValue("@grade", request.Grade);
                    cmd.Parameters.AddWithValue("@stream", request.Stream ?? "");
                    cmd.Parameters.AddWithValue("@prev", request.PreviousSchool ?? "");
                    cmd.Parameters.AddWithValue("@photo", (object?)photoPath ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@medical", request.MedicalInfo ?? "");

                    int studentId = (int)(await cmd.ExecuteScalarAsync())!;

                    // Insert guardian record
                    await using var g = new NpgsqlCommand($@"
                        INSERT INTO ""{schema}"".""Guardians""
                        (student_id, full_name, relationship, phone, email, is_primary)
                        VALUES
                        (@sid, @name, @rel, @phone, @email, TRUE);", conn, transaction);

                    g.Parameters.AddWithValue("@sid", studentId);
                    g.Parameters.AddWithValue("@name", request.GuardianName);
                    g.Parameters.AddWithValue("@rel", request.GuardianRelationship);
                    g.Parameters.AddWithValue("@phone", request.GuardianPhone);
                    g.Parameters.AddWithValue("@email", request.GuardianEmail ?? "");

                    await g.ExecuteNonQueryAsync();

                    // Commit transaction
                    await transaction.CommitAsync();

                    _logger.LogInformation("Registration successful: Student {AccountNo} (ID: {StudentId}) at {SchoolName}",
                        accountNo, studentId, schoolName);

                    return Json(new
                    {
                        success = true,
                        accountNo,
                        studentId,
                        photoPath,
                        message = "Student registered successfully"
                    });
                }
                catch
                {
                    await transaction.RollbackAsync();
                    throw;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Registration failed for {StudentName} at {SchoolName}",
                    request?.FullName, schoolName);

                return Json(new
                {
                    success = false,
                    message = "Registration failed. Please try again."
                });
            }
        }
        // =========================
// GET ALL STUDENTS
// =========================
[HttpGet("api/students")]
public async Task<IActionResult> GetStudents(string schoolName)
{
    if (!Authorized(schoolName))
    {
        _logger.LogWarning("Unauthorized students access for {SchoolName}", schoolName);
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
                id, 
                account_no, 
                full_name, 
                date_of_birth,
                gender,
                grade, 
                stream, 
                admission_date, 
                status,
                previous_school,
                medical_info
            FROM ""{schema}"".""Students""
            ORDER BY full_name", conn))
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                students.Add(new
                {
                    id = reader.GetInt32(0),
                    accountNo = reader.GetString(1),
                    fullName = reader.GetString(2),
                    dateOfBirth = reader.IsDBNull(3) ? (DateTime?)null : reader.GetDateTime(3),
                    gender = reader.IsDBNull(4) ? "-" : reader.GetString(4),
                    grade = reader.GetString(5),
                    stream = reader.IsDBNull(6) ? "-" : reader.GetString(6),
                    admissionDate = reader.GetDateTime(7),
                    status = reader.GetString(8),
                    previousSchool = reader.IsDBNull(9) ? "-" : reader.GetString(9),
                    medicalInfo = reader.IsDBNull(10) ? "-" : reader.GetString(10)
                });
            }
        }

        _logger.LogInformation("Retrieved {StudentCount} students for {SchoolName}", students.Count, schoolName);

        return Json(new { success = true, students });
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Error getting students for {SchoolName}", schoolName);
        return Json(new { success = false, message = "Error retrieving students" });
    }
}
        // Add these endpoints to your AdmissionController class

// =========================
// UPDATE STUDENT
// =========================
[HttpPut("api/students/{id}")]
[ValidateAntiForgeryToken]
public async Task<IActionResult> UpdateStudent(string schoolName, int id, [FromBody] UpdateStudentRequest req)
{
    _logger.LogInformation("Update started for student {StudentId} at {SchoolName}", id, schoolName);

    if (!Authorized(schoolName))
    {
        _logger.LogWarning("Unauthorized update attempt for {SchoolName}", schoolName);
        return Json(new { success = false, message = "Unauthorized" });
    }

    if (req == null)
    {
        return Json(new { success = false, message = "Invalid data" });
    }

    try
    {
        var (tenantId, schema) = await ResolveTenantAsync(schoolName);

        await using var conn = new NpgsqlConnection(_conn);
        await conn.OpenAsync();

        // Update student record
        await using var cmd = new NpgsqlCommand($@"
            UPDATE ""{schema}"".""Students""
            SET 
                full_name = @name,
                date_of_birth = @dob,
                gender = @gender,
                grade = @grade,
                stream = @stream,
                previous_school = @prev,
                medical_info = @medical
            WHERE id = @id
            RETURNING id;", conn);

        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@name", req.FullName);
        cmd.Parameters.AddWithValue("@dob", req.DateOfBirth);
        cmd.Parameters.AddWithValue("@gender", req.Gender);
        cmd.Parameters.AddWithValue("@grade", req.Grade);
        cmd.Parameters.AddWithValue("@stream", req.Stream ?? "");
        cmd.Parameters.AddWithValue("@prev", req.PreviousSchool ?? "");
        cmd.Parameters.AddWithValue("@medical", req.MedicalInfo ?? "");

        var result = await cmd.ExecuteScalarAsync();
        if (result == null)
        {
            _logger.LogWarning("Student not found: {StudentId} at {SchoolName}", id, schoolName);
            return Json(new { success = false, message = "Student not found" });
        }

        // Update guardian if provided
        if (!string.IsNullOrEmpty(req.GuardianName))
        {
            await using var gCmd = new NpgsqlCommand($@"
                UPDATE ""{schema}"".""Guardians""
                SET 
                    full_name = @name,
                    relationship = @rel,
                    phone = @phone,
                    email = @email
                WHERE student_id = @sid AND is_primary = TRUE;", conn);

            gCmd.Parameters.AddWithValue("@sid", id);
            gCmd.Parameters.AddWithValue("@name", req.GuardianName);
            gCmd.Parameters.AddWithValue("@rel", req.GuardianRelationship ?? "");
            gCmd.Parameters.AddWithValue("@phone", req.GuardianPhone ?? "");
            gCmd.Parameters.AddWithValue("@email", req.GuardianEmail ?? "");

            await gCmd.ExecuteNonQueryAsync();
        }

        _logger.LogInformation("Update successful for student {StudentId} at {SchoolName}", id, schoolName);

        return Json(new
        {
            success = true,
            message = "Student updated successfully"
        });
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Update failed for student {StudentId} at {SchoolName}", id, schoolName);
        return Json(new { success = false, message = "Update failed. Please try again." });
    }
}

// =========================
// APPROVE STUDENT
// =========================
[HttpPost("api/students/{id}/approve")]
[ValidateAntiForgeryToken]
public async Task<IActionResult> ApproveStudent(string schoolName, int id, [FromBody] ApproveRequest req)
{
    _logger.LogInformation("Approval started for student {StudentId} at {SchoolName}", id, schoolName);

    if (!Authorized(schoolName))
    {
        _logger.LogWarning("Unauthorized approval attempt for {SchoolName}", schoolName);
        return Json(new { success = false, message = "Unauthorized" });
    }

    try
    {
        var (tenantId, schema) = await ResolveTenantAsync(schoolName);

        await using var conn = new NpgsqlConnection(_conn);
        await conn.OpenAsync();

        // Update student status to Approved
        await using var cmd = new NpgsqlCommand($@"
            UPDATE ""{schema}"".""Students""
            SET status = @status
            WHERE id = @id
            RETURNING id;", conn);

        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@status", "Approved");

        var result = await cmd.ExecuteScalarAsync();
        if (result == null)
        {
            _logger.LogWarning("Student not found for approval: {StudentId} at {SchoolName}", id, schoolName);
            return Json(new { success = false, message = "Student not found" });
        }

        _logger.LogInformation("Approval successful for student {StudentId} at {SchoolName}", id, schoolName);

        return Json(new
        {
            success = true,
            message = "Student approved successfully"
        });
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Approval failed for student {StudentId} at {SchoolName}", id, schoolName);
        return Json(new { success = false, message = "Approval failed. Please try again." });
    }
}

// =========================
// DELETE STUDENT
// =========================
[HttpDelete("api/students/{id}")]
[ValidateAntiForgeryToken]
public async Task<IActionResult> DeleteStudent(string schoolName, int id)
{
    _logger.LogInformation("Delete started for student {StudentId} at {SchoolName}", id, schoolName);

    if (!Authorized(schoolName))
    {
        _logger.LogWarning("Unauthorized delete attempt for {SchoolName}", schoolName);
        return Json(new { success = false, message = "Unauthorized" });
    }

    try
    {
        var (tenantId, schema) = await ResolveTenantAsync(schoolName);

        await using var conn = new NpgsqlConnection(_conn);
        await conn.OpenAsync();

        // Start transaction for data consistency
        await using var transaction = await conn.BeginTransactionAsync();

        try
        {
            // Delete guardians first (foreign key constraint)
            await using var gCmd = new NpgsqlCommand(
                $@"DELETE FROM ""{schema}"".""Guardians"" WHERE student_id = @id;", conn, transaction);
            gCmd.Parameters.AddWithValue("@id", id);
            await gCmd.ExecuteNonQueryAsync();

            // Delete student record
            await using var sCmd = new NpgsqlCommand(
                $@"DELETE FROM ""{schema}"".""Students"" WHERE id = @id RETURNING id;", conn, transaction);
            sCmd.Parameters.AddWithValue("@id", id);

            var result = await sCmd.ExecuteScalarAsync();
            if (result == null)
            {
                await transaction.RollbackAsync();
                _logger.LogWarning("Student not found for deletion: {StudentId} at {SchoolName}", id, schoolName);
                return Json(new { success = false, message = "Student not found" });
            }

            await transaction.CommitAsync();

            _logger.LogInformation("Delete successful for student {StudentId} at {SchoolName}", id, schoolName);

            return Json(new
            {
                success = true,
                message = "Student deleted successfully"
            });
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Delete failed for student {StudentId} at {SchoolName}", id, schoolName);
        return Json(new { success = false, message = "Delete failed. Please try again." });
    }
}

        // =========================
        // OPTIMIZED HELPER METHODS
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

        private async Task<string> GenerateAccountNoAsync(int tenantId, NpgsqlConnection conn, string schema)
        {
            bool isUnique = false;
            var random = new Random();

            string accountNo; // no prefix

            do
            {
                // Generate 6 or 7 digit number
                int digits = random.Next(6, 8); // 6 or 7 digits
                int number = random.Next((int)Math.Pow(10, digits - 1), (int)Math.Pow(10, digits) - 1);

                accountNo = number.ToString();

                // Check if this accountNo already exists
                await using var cmd = new NpgsqlCommand(
                    $@"SELECT COUNT(*) FROM ""{schema}"".""Students"" WHERE ""account_no"" = @acc", conn);
                cmd.Parameters.AddWithValue("acc", accountNo);

                int count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                if (count == 0) isUnique = true;

            } while (!isUnique);

            return accountNo;
        }

        private async Task<string> SavePhotoAsync(IFormFile photo, string schema, string accountNo)
        {
            // Validate file type
            var allowedExtensions = new[] { ".jpg", ".jpeg", ".png" };
            var extension = Path.GetExtension(photo.FileName).ToLowerInvariant();

            if (!allowedExtensions.Contains(extension))
            {
                _logger.LogWarning("Invalid file type attempted: {Extension}", extension);
                throw new InvalidOperationException("Only JPG and PNG images are allowed");
            }

            // Validate file size (5MB max)
            if (photo.Length > 5 * 1024 * 1024)
            {
                _logger.LogWarning("File too large: {Size} bytes", photo.Length);
                throw new InvalidOperationException("Photo size must be less than 5MB");
            }

            // Create directory
            var uploadsFolder = Path.Combine(
                Directory.GetCurrentDirectory(),
                "wwwroot", "uploads", "photos", schema);
            Directory.CreateDirectory(uploadsFolder);

            // Generate unique filename
            var fileName = $"{accountNo}_{Guid.NewGuid()}{extension}";
            var filePath = Path.Combine(uploadsFolder, fileName);

            // Save file asynchronously
            await using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
            await photo.CopyToAsync(stream);

            var photoPath = $"/uploads/photos/{schema}/{fileName}";
            _logger.LogInformation("Photo saved: {PhotoPath}", photoPath);

            return photoPath;
        }
    }

    // =========================
    // DTOs
    // =========================
    public class GradeDto
    {
        public required string Name { get; set; }
        public required FeeDto Fees { get; set; }
        public List<string> Streams { get; set; } = new();
    }

    public class FeeDto
    {
        public int Term1 { get; set; }
        public int Term2 { get; set; }
        public int Term3 { get; set; }
    }

    public class AdmissionConfigResponse
    {
        public bool Success { get; set; }
        public List<GradeDto> Grades { get; set; } = new();
        public List<string> Streams { get; set; } = new();
        public required string GenderPolicy { get; set; }
    }

    public class StudentRegistrationRequest
    {
        public required string FullName { get; set; }
        public DateTime DateOfBirth { get; set; }
        public required string Gender { get; set; }
        public required string Grade { get; set; }
        public string? Stream { get; set; }
        public string? PreviousSchool { get; set; }
        public string? MedicalInfo { get; set; }
        public IFormFile? Photo { get; set; }
        public required string GuardianName { get; set; }
        public required string GuardianRelationship { get; set; }
        public required string GuardianPhone { get; set; }
        public string? GuardianEmail { get; set; }
    }

    public class ActivityLogDto
    {
        public int Id { get; set; }
        public required string Type { get; set; }
        public required string Title { get; set; }
        public required string Description { get; set; }
        public DateTime Timestamp { get; set; }
    }
    // =========================
// NEW DTOs
// =========================
public class UpdateStudentRequest
{
    public required string FullName { get; set; }
    public DateTime DateOfBirth { get; set; }
    public required string Gender { get; set; }
    public required string Grade { get; set; }
    public string? Stream { get; set; }
    public string? PreviousSchool { get; set; }
    public string? MedicalInfo { get; set; }
    public required string GuardianName { get; set; }
    public required string GuardianRelationship { get; set; }
    public required string GuardianPhone { get; set; }
    public string? GuardianEmail { get; set; }
}

public class ApproveRequest
{
    // Can extend with additional approval details if needed
    public string? Notes { get; set; }
}
}