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
using AppWebCentralRestaurante.Services; // ICohereService, CohereChatService, RecommendationService

var builder = WebApplication.CreateBuilder(args);
var configuration = builder.Configuration;
var env = builder.Environment;

// ----------------------
// MVC / Razor
// ----------------------
builder.Services.AddControllersWithViews();

// ----------------------
// DbContext (SQL Server) - ajusta connection string en appsettings.json
// ----------------------
builder.Services.AddDbContext<CentralContext>(opts =>
    opts.UseSqlServer(configuration.GetConnectionString("DefaultConnection")));

// ----------------------
// Session (para chat draft, etc.)
// ----------------------
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromMinutes(30);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
});

// ----------------------
// HttpClient y servicios externos
// - Registramos ICohereService con HttpClient factory (mejor para llamadas HTTP)
// ----------------------
builder.Services.AddHttpClient(); // keep a generic factory available
builder.Services.AddHttpClient<ICohereService, CohereChatService>();

// ----------------------
// RecommendationService: Scoped (para poder inyectar CentralContext si se necesita)
// - Scoped is recommended because it can depend on CentralContext (which is scoped).
// ----------------------
builder.Services.AddScoped<RecommendationService>();

// ----------------------
// Password hasher (Identity helper) - usado en RegistroController
// ----------------------
builder.Services.AddScoped<IPasswordHasher<Usuario>, PasswordHasher<Usuario>>();

// ----------------------
// Authentication: Cookie-based
// ----------------------
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Auth/Login";
        options.Cookie.Name = "CentralAuth";
        options.ExpireTimeSpan = TimeSpan.FromDays(7);
        options.SlidingExpiration = true;
        // options.AccessDeniedPath = "/Auth/AccessDenied"; // si quieres
    });
builder.Services.AddAuthorization();

// ----------------------
// (Opcional) Exponer wwwroot como spa-static (si usas archivos client)
// ----------------------
builder.Services.AddSpaStaticFiles(cfg => cfg.RootPath = "wwwroot");

// ----------------------
// Inicializar Firebase Admin (Service Account)
// ----------------------
var serviceAccountPath = configuration["Firebase:ServiceAccountPath"];
if (!string.IsNullOrWhiteSpace(serviceAccountPath))
{
    var resolved = Path.IsPathRooted(serviceAccountPath)
        ? serviceAccountPath
        : Path.Combine(env.ContentRootPath, serviceAccountPath);

    if (File.Exists(resolved))
    {
        try
        {
            if (FirebaseApp.DefaultInstance == null)
            {
                var cred = GoogleCredential.FromFile(resolved);
                FirebaseApp.Create(new AppOptions { Credential = cred });
                Console.WriteLine("Firebase Admin inicializado desde: " + resolved);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Error inicializando Firebase Admin: " + ex.Message);
        }
    }
    else
    {
        Console.WriteLine("ADVERTENCIA: Firebase service account no encontrado en: " + resolved);
    }
}
else
{
    Console.WriteLine("ADVERTENCIA: Firebase:ServiceAccountPath no configurado. Si usas Firebase Admin, configura la ruta.");
}

// ----------------------
// Build app
// ----------------------
var app = builder.Build();

// ----------------------
// Pipeline
// ----------------------
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

if (!app.Environment.IsDevelopment())
{
    app.UseSpaStaticFiles();
}

// Order matters: routing -> session -> auth
app.UseRouting();

// Session must be before auth if session values are used in auth callbacks/middleware
app.UseSession();

app.UseAuthentication();
app.UseAuthorization();

// Map MVC routes
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

// Finalize
app.Run();
