using AppWebCentralRestaurante.Data;
using AppWebCentralRestaurante.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;

namespace AppWebCentralRestaurante.Controllers
{
    public class PersonalController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly IPasswordHasher<Usuario> _passwordHasher;
        private readonly IConfiguration _configuration;

        public PersonalController(ApplicationDbContext context, IPasswordHasher<Usuario> passwordHasher, IConfiguration configuration)
        {
            _context = context;
            _passwordHasher = passwordHasher;
            _configuration = configuration;
        }

        // GET: /Personal/Login
        [HttpGet]
        public IActionResult Login()
        {
            return View();
        }

        // POST: /Personal/Login
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Login(PersonalLoginViewModel model)
        {
            if (!ModelState.IsValid)
            {
                return View(model);
            }

            var usuario = await _context.Usuarios.FirstOrDefaultAsync(u => u.Email == model.Email);

            if (usuario == null || (usuario.Rol != "Mozo" && usuario.Rol != "Chef" && usuario.Rol != "Admin"))
            {
                ModelState.AddModelError(string.Empty, "Credenciales invalidas o no es personal autorizado.");
                return View(model);
            }

            var passwordVerificationResult = _passwordHasher.VerifyHashedPassword(usuario, usuario.PasswordHash, model.Password);
            if (passwordVerificationResult == PasswordVerificationResult.Failed)
            {
                ModelState.AddModelError(string.Empty, "Credenciales invalidas.");
                return View(model);
            }

            var token = GenerateJwtToken(usuario);

            // For simplicity, we'll pass the token in a secure cookie.
            // In a real-world scenario, you might handle this differently (e.g., local storage for a SPA).
            Response.Cookies.Append("jwt", token, new Microsoft.AspNetCore.Http.CookieOptions
            {
                HttpOnly = true,
                Secure = true, // Set to true if using HTTPS
                SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Strict
            });

            switch (usuario.Rol)
            {
                case "Mozo":
                    return RedirectToAction("Index", "Mozo");
                case "Chef":
                    return RedirectToAction("Index", "Chef");
                case "Admin":
                    return RedirectToAction("Index", "Admin");
                default:
                    return RedirectToAction("Index", "Home");
            }
        }

        private string GenerateJwtToken(Usuario usuario)
        {
            var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_configuration["Jwt:Secret"]));
            var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);

            var claims = new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, usuario.Email),
                new Claim(ClaimTypes.NameIdentifier, usuario.Id.ToString()),
                new Claim(ClaimTypes.Role, usuario.Rol),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
            };

            var token = new JwtSecurityToken(
                issuer: _configuration["Jwt:Issuer"],
                audience: _configuration["Jwt:Audience"],
                claims: claims,
                expires: DateTime.Now.AddMinutes(120),
                signingCredentials: credentials);

            return new JwtSecurityTokenHandler().WriteToken(token);
        }
    }
}