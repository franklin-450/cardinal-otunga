using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.SignalR;
using Npgsql;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using EduTrackTrial.Services;
using EduTrackTrial.DTOs;
using EduTrackTrial.Hubs;

namespace EduTrackTrial.Controllers
{
    [Route("parent")]
    public class ParentsController : Controller
    {
        private readonly string _conn;
        private readonly ILogger<ParentsController> _logger;
        private readonly IHubContext<NotificationHub> _hub;
        private readonly IMpesaDarajaService _mpesa;

        // Locked to Cardinal Otunga (production choice)
        private const string SCHOOL_SCHEMA = "cardinal_otunga";
        private const string SCHOOL_NAME = "Cardinal Otunga High School Mosocho";

        public ParentsController(
            IConfiguration config,
            ILogger<ParentsController> logger,
            IHubContext<NotificationHub> hub,
            IMpesaDarajaService mpesa)
        {
            _conn = config.GetConnectionString("DefaultConnection")
                ?? throw new ArgumentException("DefaultConnection is not configured");

            _logger = logger;
            _hub = hub;
            _mpesa = mpesa;
        }

        // =========================
        // VIEW
        // =========================
        [HttpGet("")]
        [HttpGet("index")]
        public IActionResult Index()
        {
            ViewData["SchoolName"] = SCHOOL_NAME;
            return View("Index");
        }

        // =========================
        // STUDENT LOGIN
        // =========================
        [HttpPost("api/student-login")]
        public async Task<IActionResult> StudentLogin([FromBody] StudentLoginRequest request)
        {
            if (string.IsNullOrWhiteSpace(request?.AccountNo) ||
                string.IsNullOrWhiteSpace(request?.StudentName))
            {
                return Json(new { success = false, message = "Account number and name required" });
            }

            try
            {
                await using var conn = new NpgsqlConnection(_conn);
                await conn.OpenAsync();

                var student = await GetStudentAsync(conn, request);
                if (student == null)
                    return Json(new { success = false, message = "Invalid account number or name" });

                student.Fees = await GetStudentFeeInfoAsync(student.Id, conn);

                // Store logged-in student in session (security)
                HttpContext.Session.SetInt32("StudentId", student.Id);

                return Json(new { success = true, student });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Student login failed");
                return Json(new { success = false, message = "Unable to login at this time" });
            }
        }

        // =========================
        // PAYMENTS
        // =========================
        [HttpPost("api/initiate-payment")]
        public async Task<IActionResult> InitiatePayment([FromBody] PaymentInitiateRequest request)
        {
            int? sessionStudentId = HttpContext.Session.GetInt32("StudentId");
            if (sessionStudentId == null || sessionStudentId != request.StudentId)
                return Unauthorized(new { success = false, message = "Unauthorized" });

            if (request.Amount <= 0 || string.IsNullOrWhiteSpace(request.Phone))
                return Json(new { success = false, message = "Invalid payment request" });

            await using var conn = new NpgsqlConnection(_conn);
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();

            try
            {
                string txId = Guid.NewGuid().ToString("N")[..12].ToUpper();
                string reference = $"COHS{request.StudentId}{DateTime.UtcNow:yyyyMMddHHmmss}";

                await using (var cmd = new NpgsqlCommand($@"
                    INSERT INTO ""{SCHOOL_SCHEMA}"".""Payments""
                    (student_id, amount, phone, payment_method, status, transaction_id, reference, created_at)
                    VALUES (@sid,@amt,@phone,'MPesa','Pending',@tx,@ref,NOW());", conn))
                {
                    cmd.Parameters.AddWithValue("@sid", request.StudentId);
                    cmd.Parameters.AddWithValue("@amt", request.Amount);
                    cmd.Parameters.AddWithValue("@phone", request.Phone);
                    cmd.Parameters.AddWithValue("@tx", txId);
                    cmd.Parameters.AddWithValue("@ref", reference);
                    await cmd.ExecuteNonQueryAsync();
                }

                var result = await _mpesa.SendStkPushAsync(
                    request.Phone, request.Amount, reference);

                if (!result.Success)
                {
                    await tx.RollbackAsync();
                    return Json(new { success = false, message = result.Message });
                }

                await UpdatePaymentStatus(conn, txId, "Completed");

                await CreateNotification(
                    conn,
                    request.StudentId,
                    "Payment Received",
                    $"Payment of KES {request.Amount:N0} has been received. Thank you!");

                await tx.CommitAsync();

                // Optional real-time push
                await _hub.Clients.All.SendAsync(
                    "paymentUpdate",
                    request.StudentId,
                    request.Amount);

                return Json(new
                {
                    success = true,
                    transactionId = txId,
                    reference
                });
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync();
                _logger.LogError(ex, "Payment processing failed");
                return Json(new { success = false, message = "Payment failed" });
            }
        }

        // =========================
        // NOTIFICATIONS
        // =========================
        [HttpGet("api/notifications")]
        public async Task<IActionResult> GetNotifications(int studentId)
        {
            int? sessionStudentId = HttpContext.Session.GetInt32("StudentId");
            if (sessionStudentId == null || sessionStudentId != studentId)
                return Unauthorized();

            try
            {
                await using var conn = new NpgsqlConnection(_conn);
                await conn.OpenAsync();

                var list = new List<object>();

                await using var cmd = new NpgsqlCommand($@"
                    SELECT id,title,message,type,is_read,created_at
                    FROM ""{SCHOOL_SCHEMA}"".""Notifications""
                    WHERE student_id=@id
                    ORDER BY created_at DESC
                    LIMIT 50;", conn);

                cmd.Parameters.AddWithValue("@id", studentId);

                await using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync())
                {
                    list.Add(new
                    {
                        id = r.GetInt32(0),
                        title = r.GetString(1),
                        message = r.GetString(2),
                        type = r.GetString(3),
                        isRead = r.GetBoolean(4),
                        createdAt = r.GetDateTime(5)
                    });
                }

                return Json(new { success = true, notifications = list });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Notification fetch failed");
                return Json(new { success = false, message = "Unable to load notifications" });
            }
        }

        [HttpPost("api/notifications/mark-read")]
        public async Task<IActionResult> MarkNotificationsRead([FromBody] MarkNotificationsRequest request)
        {
            int? sessionStudentId = HttpContext.Session.GetInt32("StudentId");
            if (sessionStudentId == null || sessionStudentId != request.StudentId)
                return Unauthorized();

            await using var conn = new NpgsqlConnection(_conn);
            await conn.OpenAsync();

            if (request.NotificationIds != null && request.NotificationIds.Count > 0)
            {
                await using var cmd = new NpgsqlCommand($@"
                    UPDATE ""{SCHOOL_SCHEMA}"".""Notifications""
                    SET is_read = TRUE
                    WHERE student_id=@sid AND id = ANY(@ids);", conn);

                cmd.Parameters.AddWithValue("@sid", request.StudentId);
                cmd.Parameters.AddWithValue("@ids", request.NotificationIds);
                await cmd.ExecuteNonQueryAsync();
            }
            else
            {
                await using var cmd = new NpgsqlCommand($@"
                    UPDATE ""{SCHOOL_SCHEMA}"".""Notifications""
                    SET is_read = TRUE
                    WHERE student_id=@sid;", conn);

                cmd.Parameters.AddWithValue("@sid", request.StudentId);
                await cmd.ExecuteNonQueryAsync();
            }

            return Json(new { success = true });
        }

        // =========================
        // HELPERS
        // =========================
        private async Task<StudentDto?> GetStudentAsync(
            NpgsqlConnection conn,
            StudentLoginRequest request)
        {
            var sql = $@"
                SELECT id, account_no, full_name, date_of_birth, gender, grade,
                       stream, admission_date, previous_school, photo_path,
                       medical_info, status
                FROM ""{SCHOOL_SCHEMA}"".""Students""
                WHERE account_no=@account AND LOWER(full_name)=LOWER(@name)
                LIMIT 1;";

            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@account", request.AccountNo);
            cmd.Parameters.AddWithValue("@name", request.StudentName);

            await using var r = await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync()) return null;

            return new StudentDto
            {
                Id = r.GetInt32(0),
                AccountNo = r.GetString(1),
                FullName = r.GetString(2),
                DateOfBirth = r.GetDateTime(3),
                Gender = r.GetString(4),
                Grade = r.GetString(5),
                Stream = r.IsDBNull(6) ? null : r.GetString(6),
                AdmissionDate = r.GetDateTime(7),
                PreviousSchool = r.IsDBNull(8) ? null : r.GetString(8),
                PhotoPath = r.IsDBNull(9) ? null : r.GetString(9),
                MedicalInfo = r.IsDBNull(10) ? null : r.GetString(10),
                Status = r.IsDBNull(11) ? null : r.GetString(11)
            };
        }

        private async Task<FeeInfoDto> GetStudentFeeInfoAsync(
            int studentId,
            NpgsqlConnection conn)
        {
            int t1 = 0, t2 = 0, t3 = 0;

            await using (var cmd = new NpgsqlCommand($@"
                SELECT g.term1_fee, g.term2_fee, g.term3_fee
                FROM ""{SCHOOL_SCHEMA}"".""Students"" s
                JOIN ""{SCHOOL_SCHEMA}"".""Grades"" g ON s.grade=g.grade_name
                WHERE s.id=@id;", conn))
            {
                cmd.Parameters.AddWithValue("@id", studentId);
                await using var r = await cmd.ExecuteReaderAsync();
                if (await r.ReadAsync())
                {
                    t1 = r.GetInt32(0);
                    t2 = r.GetInt32(1);
                    t3 = r.GetInt32(2);
                }
            }

            int paid;
            await using (var cmd = new NpgsqlCommand($@"
                SELECT COALESCE(SUM(amount),0)
                FROM ""{SCHOOL_SCHEMA}"".""Payments""
                WHERE student_id=@id AND status='Completed';", conn))
            {
                cmd.Parameters.AddWithValue("@id", studentId);
                paid = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            }

            return new FeeInfoDto
            {
                Term1 = t1,
                Term2 = t2,
                Term3 = t3,
                TotalDue = t1 + t2 + t3,
                AmountPaid = paid,
                Balance = Math.Max(0, (t1 + t2 + t3) - paid)
            };
        }

        private async Task CreateNotification(
            NpgsqlConnection conn,
            int studentId,
            string title,
            string message,
            string type = "info")
        {
            await using var cmd = new NpgsqlCommand($@"
                INSERT INTO ""{SCHOOL_SCHEMA}"".""Notifications""
                (student_id,title,message,type,is_read,created_at)
                VALUES (@sid,@title,@msg,@type,FALSE,NOW());", conn);

            cmd.Parameters.AddWithValue("@sid", studentId);
            cmd.Parameters.AddWithValue("@title", title);
            cmd.Parameters.AddWithValue("@msg", message);
            cmd.Parameters.AddWithValue("@type", type);

            await cmd.ExecuteNonQueryAsync();
        }

        private async Task UpdatePaymentStatus(
            NpgsqlConnection conn,
            string txId,
            string status)
        {
            await using var cmd = new NpgsqlCommand($@"
                UPDATE ""{SCHOOL_SCHEMA}"".""Payments""
                SET status=@s, completed_at=NOW()
                WHERE transaction_id=@t;", conn);

            cmd.Parameters.AddWithValue("@s", status);
            cmd.Parameters.AddWithValue("@t", txId);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    public class MarkNotificationsRequest
    {
        public int StudentId { get; set; }
        public List<int>? NotificationIds { get; set; }
    }
}
