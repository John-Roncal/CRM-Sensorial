using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using AppWebCentralRestaurante.Data;
using AppWebCentralRestaurante.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AppWebCentralRestaurante.Controllers
{
    public class LoginViewModel
    {
        public string ReturnUrl { get; set; }
        public string Email { get; set; }
        public string Password { get; set; }
        public bool RememberMe { get; set; } = false;
    }

    public class AuthController : Controller
    {
        private readonly CentralContext _context;
        private readonly IPasswordHasher<Usuario> _passwordHasher;
        private readonly ILogger<AuthController> _logger;

        public AuthController(CentralContext context, IPasswordHasher<Usuario> passwordHasher, ILogger<AuthController> logger)
        {
            _context = context;
            _passwordHasher = passwordHasher;
            _logger = logger;
        }

        // GET: /Auth/Login
        [HttpGet]
        public IActionResult Login(string returnUrl = null)
        {
            var model = new LoginViewModel { ReturnUrl = returnUrl };
            return View(model);
        }

        // POST: /Auth/Login
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Login(LoginViewModel model)
        {
            // Evitar que ReturnUrl vacío provoque error de validación
            if (model != null && string.IsNullOrEmpty(model.ReturnUrl))
            {
                if (ModelState.ContainsKey(nameof(model.ReturnUrl)))
                    ModelState.Remove(nameof(model.ReturnUrl));
            }

            if (!ModelState.IsValid)
                return View(model);

            if (string.IsNullOrWhiteSpace(model.Email) || string.IsNullOrWhiteSpace(model.Password))
            {
                ModelState.AddModelError(string.Empty, "Email y contraseña son requeridos.");
                return View(model);
            }

            try
            {
                var usuario = await _context.Usuarios
                    .AsNoTracking()
                    .FirstOrDefaultAsync(u => u.Email == model.Email.Trim());

                if (usuario == null)
                {
                    ModelState.AddModelError(string.Empty, "No existe una cuenta asociada a ese correo.");
                    _logger.LogInformation("Intento de login con email no existente: {Email}", model.Email);
                    return View(model);
                }

                if (string.IsNullOrWhiteSpace(usuario.PasswordHash))
                {
                    ModelState.AddModelError(string.Empty, "Esta cuenta no tiene una contraseña local. Usa el inicio por enlace o restablece la contraseña.");
                    return View(model);
                }

                var verify = _passwordHasher.VerifyHashedPassword(usuario, usuario.PasswordHash, model.Password);
                if (verify == PasswordVerificationResult.Failed)
                {
                    ModelState.AddModelError(string.Empty, "Contraseña incorrecta.");
                    _logger.LogWarning("Contraseña incorrecta para usuario {Email}", model.Email);
                    return View(model);
                }

                if (verify == PasswordVerificationResult.SuccessRehashNeeded)
                {
                    try
                    {
                        usuario.PasswordHash = _passwordHasher.HashPassword(usuario, model.Password);
                        usuario.ActualizadoEn = DateTime.UtcNow;
                        _context.Usuarios.Update(usuario);
                        await _context.SaveChangesAsync();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error re-hasheando password para {Email}", model.Email);
                    }
                }

                var claims = new List<Claim>
        {
            new Claim(ClaimTypes.NameIdentifier, usuario.Id.ToString()),
            new Claim(ClaimTypes.Name, usuario.Nombre ?? string.Empty),
            new Claim(ClaimTypes.Email, usuario.Email ?? string.Empty),
            new Claim(ClaimTypes.Role, usuario.Rol ?? "Cliente")
        };

                var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
                var principal = new ClaimsPrincipal(identity);

                var props = new AuthenticationProperties
                {
                    IsPersistent = model.RememberMe,
                    ExpiresUtc = model.RememberMe ? DateTimeOffset.UtcNow.AddDays(7) : (DateTimeOffset?)null
                };

                await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, props);

                HttpContext.Session.Remove("ReservationDraft");
                HttpContext.Session.Remove("ConversationMessages");

                if (!string.IsNullOrEmpty(model.ReturnUrl) && Url.IsLocalUrl(model.ReturnUrl))
                    return Redirect(model.ReturnUrl);

                return RedirectToAction("Index", "Cliente");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error en proceso de login para {Email}", model.Email);
                ModelState.AddModelError(string.Empty, "Ocurrió un error al intentar iniciar sesión. Intenta nuevamente.");
                return View(model);
            }
        }


        // POST: /Auth/Logout
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Logout()
        {
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return RedirectToAction("Index", "Home");
        }
    }
}
