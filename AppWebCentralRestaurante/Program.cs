using System;
using System.IO;
using Google.Apis.Auth.OAuth2;
using FirebaseAdmin;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using AppWebCentralRestaurante.Data;
using AppWebCentralRestaurante.Models;
using AppWebCentralRestaurante.Services;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;
var env = builder.Environment;

// MVC / Razor
builder.Services.AddControllersWithViews();

// DbContext
builder.Services.AddDbContext<ApplicationDbContext>(opts =>
    opts.UseSqlServer(config.GetConnectionString("DefaultConnection")));

// Session
// CORS: permitir el origen de tu frontend y credenciales
builder.Services.AddCors(options =>
{
    options.AddPolicy("FrontendPolicy", policy =>
    {
        policy.WithOrigins("http://localhost:3000") // <- ajusta a tu frontend (o "https://tu-dominio.com")
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials(); // importante para que las cookies viajen
    });
});

// Session (ajustar SameSite si necesitas que la cookie viaje cross-site)
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromMinutes(30);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;

    // Si frontend y backend están en diferente origen y quieres que la cookie de sesión viaje,
    // debes usar None + Secure=true (requiere HTTPS). Si estás en desarrollo usando HTTP,
    // deja Lax o usa same origin.
    options.Cookie.SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Lax;
});

// HttpClient and app services
builder.Services.AddHttpClient();
builder.Services.AddScoped<RecommendationService>();
builder.Services.AddScoped<IPasswordHasher<Usuario>, PasswordHasher<Usuario>>();

// Authentication (cookie)
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Auth/Login";
        options.Cookie.Name = "CentralAuth";
        options.ExpireTimeSpan = TimeSpan.FromDays(7);
        options.SlidingExpiration = true;

        // Para escenarios cross-site (frontend distinto), hacer:
        // options.Cookie.SameSite = SameSiteMode.None;
        // options.Cookie.SecurePolicy = CookieSecurePolicy.Always; // REQUIERE HTTPS

        // Si estás en desarrollo en http://localhost, NO pongas SecurePolicy.Always porque el navegador rechazará la cookie.
        // En producción, usa None + Secure = true.
#if !DEBUG
        options.Cookie.SameSite = Microsoft.AspNetCore.Http.SameSiteMode.None;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
#else
        // En development con http local, mantener Lax para que puedas depurar sin HTTPS.
        options.Cookie.SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
#endif
    });
builder.Services.AddAuthorization();

// static files
builder.Services.AddSpaStaticFiles(cfg => cfg.RootPath = "wwwroot");

// Firebase Admin (optional)
var serviceAccountPath = config["Firebase:ServiceAccountPath"];
if (!string.IsNullOrWhiteSpace(serviceAccountPath))
{
    var resolved = Path.IsPathRooted(serviceAccountPath) ? serviceAccountPath : Path.Combine(env.ContentRootPath, serviceAccountPath);
    if (File.Exists(resolved))
    {
        try
        {
            if (FirebaseApp.DefaultInstance == null)
            {
                var cred = GoogleCredential.FromFile(resolved);
                FirebaseApp.Create(new AppOptions { Credential = cred });
            }
        }
        catch
        {
            // ignore init error here; log elsewhere if needed
        }
    }
}

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}
else
{
    app.UseDeveloperExceptionPage();
}

app.UseStaticFiles();
if (!app.Environment.IsDevelopment()) app.UseSpaStaticFiles();

app.UseRouting();

app.UseCors("FrontendPolicy");


app.UseSession();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.MapGet("/health", () => Results.Ok(new { status = "ok", timestamp = DateTime.UtcNow }));

app.Run();