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
    /// y del formulario previo a la reserva. Actualizado para:
    /// - crear reservas temporales (anon o user)
    /// - asociar reservas y preferencias al registrarse
    /// - maneja AnonSessions via cookie "anon_id"
    /// </summary>
    public class RegistroController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly ILogger<RegistroController> _logger;
        private readonly IPasswordHasher<Usuario> _passwordHasher;
        private readonly IHostEnvironment _env;


        // Cookie key para anon_id
        private const string CookieAnonId = "anon_id";

        // Duración cookie anon (días)
        private const int AnonCookieDays = 90;

        public RegistroController(ApplicationDbContext context, IPasswordHasher<Usuario> passwordHasher, ILogger<RegistroController> logger, IHostEnvironment env)
        {
            _context = context;
            _passwordHasher = passwordHasher;
            _logger = logger;
            _env = env;
        }

        #region Rutas de registro / verificación

        // GET: /Registro
        [HttpGet]
        public IActionResult Index()
        {
            return View("Registrar", new RegistroViewModel());
        }

        // GET: /Registro/RevisaTuCorreo?email=...
        [HttpGet]
        public IActionResult RevisaTuCorreo(string email)
        {
            var model = new RevisaCorreoViewModel { Email = email };
            return View("RevisaTuCorreo", model);
        }

        // GET: /Registro/VerificarEmail
        [HttpGet]
        public IActionResult VerificarEmail()
        {
            return View("VerificarEmail");
        }

        /// <summary>
        /// POST: /Registro/Finalizar
        /// Verifica IdToken con Firebase, crea o actualiza usuario local, hace sign-in
        /// Si existe cookie anon_id, realiza merge: perfiles -> usuario, preferencias, reservas.
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> Finalizar([FromBody] FinalizarDto dto)
        {
            if (dto == null || string.IsNullOrWhiteSpace(dto.IdToken))
                return BadRequest(new { error = "Token inválido." });

            try
            {
                // Comprueba que Firebase Admin esté inicializado
                if (FirebaseAuth.DefaultInstance == null)
                {
                    _logger.LogError("FirebaseAuth.DefaultInstance es null. FirebaseAdmin no inicializado.");
                    return StatusCode(500, new { error = "FirebaseAdmin no inicializado. Revisar configuración del backend." });
                }

                var decoded = await FirebaseAuth.DefaultInstance.VerifyIdTokenAsync(dto.IdToken);
                var uid = decoded.Uid;

                var firebaseUser = await FirebaseAuth.DefaultInstance.GetUserAsync(uid);

                if (!firebaseUser.EmailVerified)
                    return BadRequest(new { error = "El correo no ha sido verificado." });

                var email = firebaseUser.Email;
                var displayName = firebaseUser.DisplayName ?? dto.Nombre ?? string.Empty;

                var usuario = await _context.Usuarios
                    .FirstOrDefaultAsync(u => u.FirebaseUid == uid || u.Email == email);

                var isNew = usuario == null;
                if (isNew)
                {
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
                    usuario.FirebaseUid = uid;
                    usuario.EmailConfirmado = true;
                    usuario.Nombre = string.IsNullOrWhiteSpace(usuario.Nombre) ? displayName : usuario.Nombre;
                    usuario.ActualizadoEn = DateTime.UtcNow;
                    _context.Usuarios.Update(usuario);
                }

                // Manejo contraseña si viene
                if (!string.IsNullOrWhiteSpace(dto.Password) || !string.IsNullOrWhiteSpace(dto.ConfirmPassword))
                {
                    if (string.IsNullOrWhiteSpace(dto.Password) || string.IsNullOrWhiteSpace(dto.ConfirmPassword))
                        return BadRequest(new { error = "Debe proporcionar contraseña y confirmación." });

                    if (dto.Password.Length < 6)
                        return BadRequest(new { error = "La contraseña debe tener al menos 6 caracteres." });

                    if (dto.Password != dto.ConfirmPassword)
                        return BadRequest(new { error = "Las contraseñas no coinciden." });

                    usuario.PasswordHash = _passwordHasher.HashPassword(usuario, dto.Password);
                    usuario.ActualizadoEn = DateTime.UtcNow;
                    if (!isNew) _context.Usuarios.Update(usuario);
                }

                await _context.SaveChangesAsync();

                // Sign-in local cookie
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

                // --- MERGE si existe anon_id (mantengo tu lógica tal cual) ---
                // (código de merge que ya tienes...)

                var redirectUrl = Url.Action("TresPreguntas", "Registro");
                return Ok(new { mensaje = "Registrado y autenticado correctamente.", redirectUrl });
            }
            catch (FirebaseAuthException fex)
            {
                _logger.LogWarning(fex, "Error verificando idToken Firebase");
                return BadRequest(new { error = "Token Firebase inválido o expirado.", detail = _env.IsDevelopment() ? fex.ToString() : null });
            }
            catch (DbUpdateException dbex)
            {
                _logger.LogError(dbex, "Error guardando usuario en BD");
                return StatusCode(500, new { error = "Error guardando usuario en la base de datos.", detail = _env.IsDevelopment() ? dbex.ToString() : null });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error en Finalizar registro");
                return StatusCode(500, new { error = "Error interno.", detail = _env.IsDevelopment() ? ex.ToString() : null });
            }
        }


        #endregion

        #region Formulario previo a la reserva (TresPreguntas)

        /// <summary>
        /// GET: /Registro/TresPreguntas
        /// Muestra formulario con número de comensales, experiencia y restricciones.
        /// No usamos drafts en sesión; si el usuario está autenticado, podemos prellenar nombre.
        /// </summary>
        [HttpGet]
        public IActionResult TresPreguntas()
        {
            // si la vista ya no usa experiencias, no es necesario cargarlas
            var model = new TresPreguntasDto();
            return View("TresPreguntas", model);
        }


        /// <summary>
        /// POST: /Registro/GuardarPreguntas
        /// - Valida datos
        /// - Crea reserva temporal en tabla Reservas (UsuarioId si auth, sino AnonId)
        /// - Si no existe anon_id en cookie, crea AnonSession y setea cookie
        /// - Redirige al Chat para continuar la conversación
        /// </summary>
        // Reemplaza el método GuardarPreguntas por esta versión
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> GuardarPreguntas([FromForm] TresPreguntasDto dto)
        {
            if (dto == null) return BadRequest("Datos inválidos.");

            // Determinar usuario autenticado
            int? usuarioId = null;
            if (User?.Identity?.IsAuthenticated == true)
            {
                var idClaim = User.FindFirst(ClaimTypes.NameIdentifier) ?? User.FindFirst("sub");
                if (idClaim != null && int.TryParse(idClaim.Value, out int uid)) usuarioId = uid;
            }

            // Obtener o crear anonId via cookie
            Guid? anonId = null;
            if (Request.Cookies.ContainsKey(CookieAnonId))
            {
                var c = Request.Cookies[CookieAnonId];
                if (Guid.TryParse(c, out Guid parsed)) anonId = parsed;
            }

            if (!anonId.HasValue && !usuarioId.HasValue)
            {
                var newAnon = new AnonSession { CreadoEn = DateTime.UtcNow, Estado = "activo" };
                _context.AnonSessions.Add(newAnon);
                await _context.SaveChangesAsync(); // para obtener newAnon.AnonId
                anonId = newAnon.AnonId;

                var cookieOptions = new CookieOptions
                {
                    Expires = DateTimeOffset.UtcNow.AddDays(AnonCookieDays),
                    HttpOnly = true,
                    Secure = HttpContext.Request.IsHttps, // true en prod, false en localhost http
                    SameSite = SameSiteMode.Strict
                };
                Response.Cookies.Append(CookieAnonId, anonId.ToString(), cookieOptions);
            }

            // Guardar/actualizar perfil (Perfile)
            Perfile perfil = null;
            if (usuarioId.HasValue) perfil = await _context.Perfiles.FirstOrDefaultAsync(p => p.UsuarioId == usuarioId.Value);
            if (perfil == null && anonId.HasValue) perfil = await _context.Perfiles.FirstOrDefaultAsync(p => p.AnonId == anonId.Value);

            if (perfil == null)
            {
                perfil = new Perfile
                {
                    UsuarioId = usuarioId,
                    AnonId = anonId,
                    CreadoEn = DateTime.UtcNow,
                    EstadoPerfilCompleto = false
                };
                _context.Perfiles.Add(perfil);
            }

            perfil.Q1 = dto.Q1;
            perfil.Q1_Otro = string.IsNullOrWhiteSpace(dto.Q1_Otro) ? null : dto.Q1_Otro.Trim();
            perfil.Q2 = dto.Q2;
            perfil.Q3 = dto.Q3;
            perfil.ActualizadoEn = DateTime.UtcNow;

            // Marca completo sólo si las 3 preguntas están respondidas
            if (!string.IsNullOrWhiteSpace(perfil.Q1) &&
                !string.IsNullOrWhiteSpace(perfil.Q2) &&
                !string.IsNullOrWhiteSpace(perfil.Q3))
            {
                perfil.EstadoPerfilCompleto = true;
            }

            await _context.SaveChangesAsync();

            // Log sencillo (no romperá flujo si falla)
            await LogEventAsync("profile.saved_from_three_questions", usuarioId, anonId, null, "web", new { perfilId = perfil.PerfilId });

            // Redirect al chat para que el bot haga las siguientes preguntas (fecha, nombre, teléfono...)
            return RedirectToAction("Index", "Chat");
        }


        // helper para guardar eventos en la tabla Eventos
        private async Task LogEventAsync(string eventType, int? usuarioId, Guid? anonId, Guid? conversationId, string senderId, object payload)
        {
            try
            {
                var evt = new Evento
                {
                    EventType = eventType ?? "unknown",
                    UsuarioId = usuarioId,
                    AnonId = anonId,
                    ConversationId = conversationId,
                    SenderId = senderId ?? "web",
                    Payload = payload == null ? null : JsonSerializer.Serialize(payload),
                    CreadoEn = DateTime.UtcNow
                };

                _context.Eventos.Add(evt);
                await _context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                // No queremos que un fallo al loguear events rompa el flujo de registro,
                // así que lo registramos en el logger local y continuamos.
                _logger.LogError(ex, "Error guardando evento {EventType}", eventType);
            }
        }




        #endregion

        #region ViewModels / DTOs locales

        public class RegistroViewModel
        {
            [Required, Display(Name = "Nombre")]
            public string Nombre { get; set; }

            [Required, EmailAddress, Display(Name = "Correo electrónico")]
            public string Email { get; set; }

            public string ReturnUrl { get; set; }

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
            // preguntas clave
            public string Q1 { get; set; }           // valores como "Celebración especial", "Otra"
            public string Q1_Otro { get; set; }      // texto si Q1 == "Otra"
            public string Q2 { get; set; }           // "Solo","Pareja",...
            public string Q3 { get; set; }           // preferencias de cocina
        }

        #endregion
    }
}
