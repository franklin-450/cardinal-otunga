using Microsoft.AspNetCore.Mvc;
using Npgsql;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace EduTrackTrial.Controllers
{
    /// <summary>
    /// SIMPLE MIGRATION CONTROLLER - Run once to fix missing tables
    /// DELETE THIS CONTROLLER after running the migration
    /// </summary>
    [Route("__platform/migration")]
    public class SimpleMigrationController(IConfiguration config, ILogger<SimpleMigrationController> logger) : Controller
    {
        private readonly string? _conn = config.GetConnectionString("DefaultConnection");
        private readonly ILogger<SimpleMigrationController> _logger = logger;

        /// <summary>
        /// SIMPLIFIED: Add missing tables to all verified tenants
        /// URL: http://localhost:5201/__platform/migration/auto-fix?secret=12345
        /// </summary>
        [HttpGet("auto-fix")]
        public async Task<IActionResult> AutoFix(string secret)
        {
            if (secret != "12345")
                return Unauthorized(new { message = "Invalid secret" });

            var results = new List<string>();

            try
            {
                await using var conn = new NpgsqlConnection(_conn);
                await conn.OpenAsync();

                // Get verified tenants
                var tenants = new List<(int id, string name, string subdomain)>();
                await using (var cmd = new NpgsqlCommand(
                    "SELECT id, name, subdomain FROM tenants WHERE verified = true", conn))
                {
                    await using var reader = await cmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        tenants.Add((
                            reader.GetInt32(0),
                            reader.GetString(1),
                            reader.GetString(2)
                        ));
                    }
                }

                results.Add($"Found {tenants.Count} verified tenant(s)");

                foreach (var (id, name, subdomain) in tenants)
                {
                    try
                    {
                        var schema = $"tenant_{subdomain}";
                        results.Add($"\n=== Processing: {name} ({subdomain}) ===");

                        // 1. Payments table
                        await CreateTableIfNotExists(conn, schema, "Payments", $@"
                            CREATE TABLE ""{schema}"".""Payments"" (
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
                            CREATE INDEX IF NOT EXISTS idx_payments_student ON ""{schema}"".""Payments"" (student_id);
                            CREATE INDEX IF NOT EXISTS idx_payments_status ON ""{schema}"".""Payments"" (status);
                            CREATE INDEX IF NOT EXISTS idx_payments_date ON ""{schema}"".""Payments"" (created_at DESC);
                        ", results);

                        // 2. Secretaries table
                        await CreateTableIfNotExists(conn, schema, "Secretaries", $@"
                            CREATE TABLE ""{schema}"".""Secretaries"" (
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
                            CREATE INDEX IF NOT EXISTS idx_secretaries_email ON ""{schema}"".""Secretaries"" (email);
                            CREATE INDEX IF NOT EXISTS idx_secretaries_active ON ""{schema}"".""Secretaries"" (is_active);
                        ", results);

                        // Ensure password_hash nullable
                        try
                        {
                            await using var alterCmd = new NpgsqlCommand($@"
                                ALTER TABLE ""{schema}"".""Secretaries"" 
                                ALTER COLUMN password_hash DROP NOT NULL;", conn);
                            await alterCmd.ExecuteNonQueryAsync();
                            results.Add("  ✓ Made password_hash nullable in Secretaries");
                        }
                        catch { }

                        // 3. StaffCredentials table
                        await CreateTableIfNotExists(conn, schema, "StaffCredentials", $@"
                            CREATE TABLE ""{schema}"".""StaffCredentials"" (
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
                            CREATE INDEX IF NOT EXISTS idx_staff_credentials_username ON ""{schema}"".""StaffCredentials"" (username);
                            CREATE INDEX IF NOT EXISTS idx_staff_credentials_secretary ON ""{schema}"".""StaffCredentials"" (secretary_id);
                        ", results);

                        // 4. SchoolInfo table
                        await CreateTableIfNotExists(conn, schema, "SchoolInfo", $@"
                            CREATE TABLE ""{schema}"".""SchoolInfo"" (
                                id SERIAL PRIMARY KEY,
                                school_name VARCHAR(255),
                                registration_number VARCHAR(100),
                                established_year INTEGER,
                                school_motto TEXT,
                                badge_url TEXT,
                                created_at TIMESTAMP DEFAULT NOW(),
                                updated_at TIMESTAMP DEFAULT NOW()
                            );
                            INSERT INTO ""{schema}"".""SchoolInfo"" (school_name, school_motto, created_at)
                            VALUES ('{name}', 'Excellence in Education', NOW())
                            ON CONFLICT DO NOTHING;
                        ", results);

                        // 5. SchoolContact table
                        await CreateTableIfNotExists(conn, schema, "SchoolContact", $@"
                            CREATE TABLE ""{schema}"".""SchoolContact"" (
                                id SERIAL PRIMARY KEY,
                                email VARCHAR(255),
                                phone VARCHAR(50),
                                address TEXT,
                                website VARCHAR(255),
                                created_at TIMESTAMP DEFAULT NOW(),
                                updated_at TIMESTAMP DEFAULT NOW()
                            );
                            INSERT INTO ""{schema}"".""SchoolContact"" (created_at)
                            VALUES (NOW())
                            ON CONFLICT DO NOTHING;
                        ", results);

                        // 6. Notifications table
                        await CreateTableIfNotExists(conn, schema, "Notifications", $@"
                            CREATE TABLE ""{schema}"".""Notifications"" (
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
                        ", results);

                        results.Add($"  ✓ Completed: {name}");
                    }
                    catch (Exception ex)
                    {
                        results.Add($"  ✗ ERROR: {ex.Message}");
                        _logger.LogError(ex, "Error processing tenant: {Subdomain}", subdomain);
                    }
                }

                return Ok(new
                {
                    success = true,
                    message = "Migration completed successfully",
                    tenantsProcessed = tenants.Count,
                    details = results
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Migration failed");
                return StatusCode(500, new
                {
                    success = false,
                    message = "Migration failed",
                    error = ex.Message,
                    details = results
                });
            }
        }

        private async Task CreateTableIfNotExists(NpgsqlConnection conn, string schema, string tableName, string createSql, List<string> results)
        {
            bool tableExists = false;
            await using (var checkCmd = new NpgsqlCommand($@"
                SELECT EXISTS (
                    SELECT FROM information_schema.tables 
                    WHERE table_schema = @schema 
                    AND table_name = @tableName
                )", conn))
            {
                checkCmd.Parameters.AddWithValue("@schema", schema);
                checkCmd.Parameters.AddWithValue("@tableName", tableName);
                tableExists = (bool)(await checkCmd.ExecuteScalarAsync() ?? false);
            }

            if (!tableExists)
            {
                await using var createCmd = new NpgsqlCommand(createSql, conn);
                await createCmd.ExecuteNonQueryAsync();
                results.Add($"  ✓ Created {tableName} table");
            }
            else
            {
                results.Add($"  → {tableName} table already exists");
            }
        }

        [HttpGet("check")]
        public async Task<IActionResult> Check(string secret)
        {
            if (secret != "12345")
                return Unauthorized(new { message = "Invalid secret" });

            var report = new List<object>();

            try
            {
                await using var conn = new NpgsqlConnection(_conn);
                await conn.OpenAsync();

                var tenants = new List<(string name, string subdomain)>();
                await using (var cmd = new NpgsqlCommand(
                    "SELECT name, subdomain FROM tenants WHERE verified = true", conn))
                {
                    await using var reader = await cmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                        tenants.Add((reader.GetString(0), reader.GetString(1)));
                }

                foreach (var (name, subdomain) in tenants)
                {
                    var schema = $"tenant_{subdomain}";

                    var tables = new Dictionary<string, bool>
                    {
                        ["Payments"] = await TableExists(conn, schema, "Payments"),
                        ["Secretaries"] = await TableExists(conn, schema, "Secretaries"),
                        ["StaffCredentials"] = await TableExists(conn, schema, "StaffCredentials"),
                        ["SchoolInfo"] = await TableExists(conn, schema, "SchoolInfo"),
                        ["SchoolContact"] = await TableExists(conn, schema, "SchoolContact"),
                        ["Notifications"] = await TableExists(conn, schema, "Notifications")
                    };

                    var allExist = tables.Values.All(v => v);
                    var status = allExist ? "✓ OK" : "✗ NEEDS MIGRATION";

                    report.Add(new
                    {
                        school = name,
                        subdomain = subdomain,
                        tables = tables,
                        status = status
                    });
                }

                return Ok(new
                {
                    success = true,
                    totalSchools = tenants.Count,
                    report = report
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Check failed");
                return StatusCode(500, new
                {
                    success = false,
                    error = ex.Message
                });
            }
        }

        private async Task<bool> TableExists(NpgsqlConnection conn, string schema, string tableName)
        {
            await using var cmd = new NpgsqlCommand($@"
                SELECT EXISTS (
                    SELECT FROM information_schema.tables 
                    WHERE table_schema = @schema AND table_name = @tableName
                )", conn);
            cmd.Parameters.AddWithValue("@schema", schema);
            cmd.Parameters.AddWithValue("@tableName", tableName);
            return (bool)(await cmd.ExecuteScalarAsync() ?? false);
        }
    }
}
