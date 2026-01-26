using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Http;
using Npgsql;
using System;
using System.Collections.Generic;
using EduTrackTrial.Models;

namespace EduTrackTrial.Controllers
{
    [Route("__platform/core/tenants-control-7f3a9c")]
    public class PlatformAdminController(
        IConfiguration config,
        ILogger<PlatformAdminController> logger) : Controller
    {
        private readonly string _conn = config.GetConnectionString("DefaultConnection") ?? string.Empty;
        private readonly IConfiguration _config = config;
        private readonly ILogger<PlatformAdminController> _logger = logger;

        // =========================
        // DASHBOARD
        // =========================
[HttpGet("")]
public IActionResult Index()
{
    LockDown();

    var tenants = new List<PlatformTenantViewModel>();

    using var conn = new NpgsqlConnection(_conn);
    // Use synchronous Open() instead of OpenAsync().Wait()
    conn.Open();

    using var cmd = new NpgsqlCommand(@"
        SELECT
            id,
            name,
            subdomain,
            verified,
            trial_expires_at,
            created_at
        FROM tenants
        ORDER BY created_at DESC;
    ", conn);

    cmd.CommandTimeout = 15;

    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
        tenants.Add(new PlatformTenantViewModel
        {
            Id = reader.GetInt32(0),
            Name = reader.GetString(1),
            Subdomain = reader.GetString(2),
            Verified = reader.GetBoolean(3),
            TrialEnds = reader.GetDateTime(4),
            CreatedAt = reader.GetDateTime(5),
            Email = string.Empty
        });
    }

    _logger.LogInformation(
        "Platform dashboard loaded | Tenants={Count}",
        tenants.Count);

    return View("Dashboard", tenants);
}

        // =========================
        // DISABLE TENANT
        // =========================
        [HttpPost("disable/{id}")]
        [ValidateAntiForgeryToken]
        public IActionResult DisableTenant(int id)
        {
            LockDown();

            using var conn = new NpgsqlConnection(_conn);
            conn.Open();

            using var cmd = new NpgsqlCommand(
                "UPDATE tenants SET verified = FALSE WHERE id = @id;",
                conn);

            cmd.CommandTimeout = 10;
            cmd.Parameters.AddWithValue("id", id);
            cmd.ExecuteNonQuery();

            _logger.LogWarning("Tenant disabled | TenantId={Id}", id);
            return RedirectToAction(nameof(Index));
        }

  [HttpPost("delete/{id}")]
[ValidateAntiForgeryToken]
public IActionResult DeleteTenant(int id)
{
    LockDown();

    using var conn = new NpgsqlConnection(_conn);
    conn.Open();

    string subdomain;

    using (var subCmd = new NpgsqlCommand(
        "SELECT subdomain FROM tenants WHERE id = @id;",
        conn))
    {
        subCmd.CommandTimeout = 5;
        subCmd.Parameters.AddWithValue("id", id);
        subdomain = (subCmd.ExecuteScalar() as string) ?? string.Empty;
    }

    if (!string.IsNullOrWhiteSpace(subdomain))
    {
        using var dropCmd = new NpgsqlCommand(
            $@"DROP SCHEMA IF EXISTS ""tenant_{subdomain}"" CASCADE;",
            conn);
        dropCmd.CommandTimeout = 10;
        dropCmd.ExecuteNonQuery();

        _logger.LogCritical("Tenant schema dropped | tenant_{Subdomain}", subdomain);
    }

    using var delCmd = new NpgsqlCommand(
        "DELETE FROM tenants WHERE id = @id;",
        conn);
    delCmd.CommandTimeout = 5;
    delCmd.Parameters.AddWithValue("id", id);
    delCmd.ExecuteNonQuery();

    _logger.LogCritical("Tenant deleted permanently | TenantId={Id}", id);

    return Json(new { success = true, id });
}


        // =========================
        // SECURITY GATE
        // =========================
        private void LockDown()
        {
            var env = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
            var sessionAdmin = HttpContext.Session.GetString("PLATFORM_ADMIN");
            var configAdmin = _config["PlatformAdmin:Email"];

            _logger.LogInformation(
                "PLATFORM LOCKDOWN | ENV={Env} | SESSION={Session} | CONFIG={Config}",
                env, sessionAdmin, configAdmin);

            if (env != "Production" && sessionAdmin == configAdmin)
                return;

            if (env != "Production")
                throw new UnauthorizedAccessException("ENV BLOCKED");

            if (sessionAdmin != configAdmin)
                throw new UnauthorizedAccessException("SESSION BLOCKED");
        }
    }
}
