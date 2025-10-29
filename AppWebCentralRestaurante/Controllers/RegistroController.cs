using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Threading.Tasks;
using AppWebCentralRestaurante.Data;
using AppWebCentralRestaurante.Models;
using FirebaseAdmin.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Security.Claims;

namespace AppWebCentralRestaurante.Controllers
{
    /// <summary>
    /// Controlador responsable del flujo de registro + verificación por correo
    /// y del formulario de "3 preguntas clave" previo al chatbot/reserva.
    /// </summary>
    public class RegistroController : Controller
    {
        private readonly CentralContext _context;
        private readonly ILogger<RegistroController> _logger;
        private readonly IPasswordHasher<Usuario> _passwordHasher;

        // Key de sesión donde guardamos temporalmente el draft de reserva
        private const string SessionKeyReservationDraft = "ReservationDraft";

        public RegistroController(CentralContext context, IPasswordHasher<Usuario> passwordHasher, ILogger<RegistroController> logger)
        {
            _context = context;
            _passwordHasher = passwordHasher;
            _logger = logger;
        }

        #region Rutas de registro / verificación

        // GET: /Registro
        [HttpGet]
        public IActionResult Index()
        {
            // Vista donde el usuario introduce Nombre + Email para iniciar registro
            return View("Registrar", new RegistroViewModel());
        }

        // GET: /Registro/RevisaTuCorreo?email=...
        [HttpGet]
        public IActionResult RevisaTuCorreo(string email)
        {
            // Vista que muestra "Te enviamos un link a tu correo ###"
            var model = new RevisaCorreoViewModel { Email = email };
            return View("RevisaTuCorreo", model);
        }

        // GET: /Registro/VerificarEmail
        [HttpGet]
        public IActionResult VerificarEmail()
        {
            // Vista que carga verify.js (cliente completará signInWithEmailLink)
            return View("VerificarEmail");
        }

        /// <summary>
        /// POST: /Registro/Finalizar
        /// Endpoint llamado desde cliente (verify.js) con { IdToken, Nombre, Password, ConfirmPassword }.
        /// - Verifica token con Firebase Admin
        /// - Crea/actualiza usuario local (tabla Usuarios)
        /// - Guarda PasswordHash si se envió password
        /// - Firma cookie de autenticación local
        /// Devuelve JSON { mensaje, redirectUrl } para que el cliente pueda redirigir a las 3 preguntas.
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> Finalizar([FromBody] FinalizarDto dto)
        {
            if (dto == null || string.IsNullOrWhiteSpace(dto.IdToken))
                return BadRequest("Token inválido.");

            try
            {
                // Verificar idToken con Firebase Admin SDK
                var decoded = await FirebaseAuth.DefaultInstance.VerifyIdTokenAsync(dto.IdToken);
                var uid = decoded.Uid;

                // Obtener registro de usuario en Firebase (email, emailVerified, displayName)
                var firebaseUser = await FirebaseAuth.DefaultInstance.GetUserAsync(uid);

                if (!firebaseUser.EmailVerified)
                    return BadRequest("El correo no ha sido verificado.");

                var email = firebaseUser.Email;
                var displayName = firebaseUser.DisplayName ?? dto.Nombre ?? string.Empty;

                // Buscar usuario local por FirebaseUid o por email
                var usuario = await _context.Usuarios
                    .FirstOrDefaultAsync(u => u.FirebaseUid == uid || u.Email == email);

                var isNew = usuario == null;
                if (isNew)
                {
                    // Nuevo usuario local: llenar datos básicos
                    usuario = new Usuario
                    {
                        Nombre = string.IsNullOrWhiteSpace(displayName) ? "Cliente" : displayName,
                        Email = email,
                        FirebaseUid = uid,
                        Rol = "Cliente",
                        EmailConfirmado = true,
                        CreadoEn = DateTime.UtcNow
                    };
                    _context.Usuarios.Add(usuario);
                }
                else
                {
                    // Actualizar campos mínimos si es necesario
                    usuario.FirebaseUid = uid;
                    usuario.EmailConfirmado = true;
                    usuario.Nombre = string.IsNullOrWhiteSpace(usuario.Nombre) ? displayName : usuario.Nombre;
                    usuario.ActualizadoEn = DateTime.UtcNow;
                    _context.Usuarios.Update(usuario);
                }

                // Si el cliente envió contraseña en el formulario, validarla y guardarla hasheada
                if (!string.IsNullOrWhiteSpace(dto.Password) || !string.IsNullOrWhiteSpace(dto.ConfirmPassword))
                {
                    if (string.IsNullOrWhiteSpace(dto.Password) || string.IsNullOrWhiteSpace(dto.ConfirmPassword))
                        return BadRequest("Debe proporcionar contraseña y confirmación.");

                    if (dto.Password.Length < 6)
                        return BadRequest("La contraseña debe tener al menos 6 caracteres.");

                    if (dto.Password != dto.ConfirmPassword)
                        return BadRequest("Las contraseñas no coinciden.");

                    // Hashear y guardar en PasswordHash
                    usuario.PasswordHash = _passwordHasher.HashPassword(usuario, dto.Password);
                    usuario.ActualizadoEn = DateTime.UtcNow;

                    // Si el usuario ya existía, aseguramos que EF detecte el cambio
                    if (!isNew) _context.Usuarios.Update(usuario);
                }

                // Guardar cambios (nuevo usuario o actualización)
                await _context.SaveChangesAsync();

                // Crear claims y cookie de autenticación local
                var claims = new List<Claim>
                {
                    new Claim(ClaimTypes.NameIdentifier, usuario.Id.ToString()),
                    new Claim(ClaimTypes.Name, usuario.Nombre ?? string.Empty),
                    new Claim(ClaimTypes.Email, usuario.Email ?? string.Empty),
                    new Claim(ClaimTypes.Role, usuario.Rol ?? "Cliente")
                };

                var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
                var principal = new ClaimsPrincipal(identity);

                await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);

                // Devolver JSON con redirect sugerido (cliente JS puede redirigir aquí)
                var redirectUrl = Url.Action("TresPreguntas", "Registro");
                return Ok(new { mensaje = "Registrado y autenticado correctamente.", redirectUrl });
            }
            catch (FirebaseAuthException fex)
            {
                _logger.LogWarning(fex, "Error verificando idToken Firebase");
                return BadRequest("Token Firebase inválido o expirado.");
            }
            catch (DbUpdateException dbex)
            {
                _logger.LogError(dbex, "Error guardando usuario en BD");
                return StatusCode(500, "Error guardando usuario en la base de datos.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error en Finalizar registro");
                return StatusCode(500, "Error interno.");
            }
        }

        #endregion

        #region Tres preguntas (formulario previo al chatbot)

        /// <summary>
        /// GET: /Registro/TresPreguntas
        /// Muestra un formulario con:
        ///  - número de comensales
        ///  - selección de experiencia (lee la tabla Experiencias)
        ///  - restricciones/alergias (texto)
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> TresPreguntas()
        {
            // Cargar experiencias desde BD para popular el select
            var experiencias = await _context.Experiencias.AsNoTracking().ToListAsync();
            ViewData["Experiencias"] = experiencias;

            // Preparar un draft inicial que puede prellenarse si el usuario ya está autenticado
            var draft = new ReservationDraft
            {
                NombreUsuario = User?.Identity?.IsAuthenticated == true ? User.FindFirst(ClaimTypes.Name)?.Value ?? User.Identity.Name : null
            };

            return View("TresPreguntas", draft);
        }

        /// <summary>
        /// POST: /Registro/GuardarPreguntas
        /// Guarda en sesión el draft con las 3 preguntas y redirige al Chat.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult GuardarPreguntas([FromForm] TresPreguntasDto dto)
        {
            // Validaciones simples
            if (dto == null)
                return BadRequest("Datos inválidos.");

            if (dto.Personas <= 0)
                ModelState.AddModelError(nameof(dto.Personas), "Número de comensales inválido.");
            if (dto.ExperienciaId <= 0)
                ModelState.AddModelError(nameof(dto.ExperienciaId), "Seleccione una experiencia.");

            if (!ModelState.IsValid)
            {
                // Si hay error, recargar la vista GET para que el usuario corrija
                return RedirectToAction(nameof(TresPreguntas));
            }

            // Construir el draft que el ChatController esperará
            var draft = new ReservationDraft
            {
                Personas = dto.Personas,
                ExperienciaId = dto.ExperienciaId,
                Restricciones = string.IsNullOrWhiteSpace(dto.Restricciones) ? null : dto.Restricciones.Trim(),
                NombreUsuario = string.IsNullOrWhiteSpace(dto.NombreUsuario) ? (User?.FindFirst(ClaimTypes.Name)?.Value ?? User?.Identity?.Name) : dto.NombreUsuario,
                FromThreeQuestions = true,
                Step = "ask_experiencia"
            };

            // Guardar draft en sesión (serializado JSON)
            HttpContext.Session.SetString(SessionKeyReservationDraft, JsonSerializer.Serialize(draft));

            // Redirigir al Chat (index) para continuar la conversación
            return RedirectToAction("Index", "Chat");
        }

        #endregion

        #region ViewModels / DTOs locales

        public class RegistroViewModel
        {
            [Required, Display(Name = "Nombre")]
            public string Nombre { get; set; }

            [Required, EmailAddress, Display(Name = "Correo electrónico")]
            public string Email { get; set; }
        }

        public class RevisaCorreoViewModel
        {
            public string Email { get; set; }
        }

        public class FinalizarDto
        {
            public string IdToken { get; set; }
            public string Nombre { get; set; }

            public string Password { get; set; }
            public string ConfirmPassword { get; set; }
        }

        public class TresPreguntasDto
        {
            public int Personas { get; set; } = 1;
            public int ExperienciaId { get; set; }
            public string Restricciones { get; set; }
            public string NombreUsuario { get; set; }
        }

        #endregion
    }
}
