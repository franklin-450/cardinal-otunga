using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using EduTrackTrial.Hubs;

var builder = WebApplication.CreateBuilder(args);

// =========================
// SERVICES
// =========================
builder.Services.AddControllersWithViews()
    .AddSessionStateTempDataProvider();

builder.Services.AddDistributedMemoryCache();

builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromMinutes(30);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
});

builder.Services.AddResponseCaching();

// Real-time notifications via SignalR (optional)
builder.Services.AddSignalR();
// Simulated M-Pesa service used in development/testing
builder.Services.AddSingleton<EduTrackTrial.Services.IMpesaDarajaService, EduTrackTrial.Services.SimulatedMpesaDarajaService>();

builder.WebHost.UseStaticWebAssets();

// =========================
// APP
// =========================
var app = builder.Build();

// =========================
// MIDDLEWARE
// =========================
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}
else
{
    app.UseDeveloperExceptionPage();
}

// Remove HTTPS redirect for Docker/Render (reverse proxy handles it)
// app.UseHttpsRedirection();

app.UseStaticFiles();

app.UseRouting();

app.UseResponseCaching();
app.UseSession();

app.UseAuthentication();
app.UseAuthorization();

// =========================
// ROUTES
// =========================
app.MapControllerRoute(
    name: "school",
    pattern: "{schoolName}/{action=Index}/{id?}",
    defaults: new { controller = "School" });

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

// SignalR hubs
app.MapHub<NotificationHub>("/notificationsHub");

// Health check endpoint for Docker
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.Run();