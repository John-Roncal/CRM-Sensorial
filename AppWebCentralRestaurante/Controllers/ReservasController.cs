using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Authorization;
using AppWebCentralRestaurante.Data;
using AppWebCentralRestaurante.Models;

namespace AppWebCentralRestaurante.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class ReservasController : ControllerBase
    {
        private readonly ApplicationDbContext _db;
        private readonly IConfiguration _config;
        private readonly ILogger<ReservasController> _logger;

        public ReservasController(ApplicationDbContext db, IConfiguration config, ILogger<ReservasController> logger)
        {
            _db = db;
            _config = config;
            _logger = logger;
        }

        // ------------------------
        // DTOs
        // ------------------------
        public class ChatStartDto { public Guid? ConversationId { get; set; } public Guid? AnonId { get; set; } public int? UserId { get; set; } public string Locale { get; set; } }
        public class ChatMessageDto
        {
            public Guid ConversationId { get; set; }
            public Guid? AnonId { get; set; }
            public int? UserId { get; set; }
            public string Message { get; set; }
            public string QKey { get; set; }
            public string QAnswer { get; set; }

            // Añadidos para compatibilidad con chat.js / FastAPI payloads
            public string ReservationField { get; set; }
            public string ReservationValue { get; set; }
        }
        public class CreateReservaDto
        {
            public Guid? AnonId { get; set; }
            public int? UserId { get; set; }
            public int ExperienciaId { get; set; }
            public int NumComensales { get; set; } = 1;
            public DateTime FechaHora { get; set; }
            public string Restricciones { get; set; }
            public string NombreReserva { get; set; }
            public string DNI { get; set; }
            public string Telefono { get; set; }
            public bool EsOcasionEspecial { get; set; } = false;
            public string ReferenciaConversationId { get; set; }
        }
        public class ConfirmReservaDto { public int ReservaId { get; set; } public string Accion { get; set; } public bool GuardarPreferencias { get; set; } = false; }
        public class MergeProfileDto { public int UserId { get; set; } public Guid AnonId { get; set; } public bool TransferEvents { get; set; } = true; public bool TransferProfile { get; set; } = true; }
        public class EventDto { public string EventType { get; set; } public int? UsuarioId { get; set; } public Guid? AnonId { get; set; } public Guid? ConversationId { get; set; } public string SenderId { get; set; } public object Payload { get; set; } }
        public class ScoreRequestDto
        {
            public string RequestId { get; set; }
            public int? UserId { get; set; }
            public Guid? AnonId { get; set; }
            public Dictionary<string, object> Context { get; set; }
            public List<Dictionary<string, object>> Candidates { get; set; }
            public int TopK { get; set; } = 5;
        }
        public class ScoreItem { public int Id { get; set; } public double Score { get; set; } }
        public class ScoreResponseDto { public string RequestId { get; set; } public string ModelVersion { get; set; } public List<ScoreItem> Scores { get; set; } public List<int> Ranked => Scores?.OrderByDescending(s => s.Score).Select(s => s.Id).ToList(); public int LatencyMs { get; set; } }

        // ------------------------
        // Helpers
        // ------------------------
        private int? GetCurrentUserId()
        {
            if (User?.Identity?.IsAuthenticated == true)
            {
                var idClaim = User.FindFirst(ClaimTypes.NameIdentifier) ?? User.FindFirst("sub") ?? User.FindFirst("id");
                if (idClaim != null && int.TryParse(idClaim.Value, out int uid)) return uid;
            }
            return null;
        }

        // Reemplazar/añadir en el controller
        private async Task<int?> GetCurrentUserIdAsync()
        {
            if (User?.Identity?.IsAuthenticated != true) return null;

            // Primero intentar parsear claims numéricas estándar
            var idClaim = User.FindFirst(ClaimTypes.NameIdentifier) ?? User.FindFirst("sub") ?? User.FindFirst("id");
            if (idClaim != null && int.TryParse(idClaim.Value, out int parsed)) return parsed;

            // Si claim no es int: intentar mapear por email
            var email = User.FindFirst(ClaimTypes.Email)?.Value ?? User.FindFirst("email")?.Value;
            if (!string.IsNullOrEmpty(email))
            {
                var user = await _db.Usuarios.FirstOrDefaultAsync(u => u.Email == email);
                if (user != null) return user.Id;
            }

            // Si no hay email, intentar por username/nombre
            var name = User.FindFirst(ClaimTypes.Name)?.Value ?? User.Identity.Name;
            if (!string.IsNullOrEmpty(name))
            {
                var userByName = await _db.Usuarios.FirstOrDefaultAsync(u => u.Nombre == name);
                if (userByName != null) return userByName.Id;
            }

            return null;
        }


        private bool IsServiceTokenValid()
        {
            var token = Request.Headers.ContainsKey("X-Service-Token") ? Request.Headers["X-Service-Token"].ToString() : null;
            var cfg = _config["ServiceToken"];
            return !string.IsNullOrEmpty(cfg) && token != null && token == cfg;
        }

        private async Task LogEventAsync(string eventType, int? usuarioId, Guid? anonId, Guid? conversationId, string senderId, object payload)
        {
            var evt = new Evento
            {
                EventType = eventType,
                UsuarioId = usuarioId,
                AnonId = anonId,
                ConversationId = conversationId,
                SenderId = senderId ?? "backend",
                Payload = payload == null ? null : System.Text.Json.JsonSerializer.Serialize(payload),
                CreadoEn = DateTime.UtcNow
            };
            _db.Eventos.Add(evt);
            await _db.SaveChangesAsync();
        }

        // ------------------------
        // POST api/reservas/chat/start
        // ------------------------
        [HttpPost("chat/start")]
        public async Task<IActionResult> ChatStart([FromBody] ChatStartDto dto)
        {
            var conversationId = dto?.ConversationId ?? Guid.NewGuid();

            int? incomingUserId = dto?.UserId;
            Guid? incomingAnonId = dto?.AnonId;

            // Si no vino anonId en body, intentar leer cookie "anon_id"
            if (!incomingAnonId.HasValue && Request.Cookies.ContainsKey("anon_id"))
            {
                if (Guid.TryParse(Request.Cookies["anon_id"], out Guid parsedCookieAnon))
                    incomingAnonId = parsedCookieAnon;
            }

            // buscar perfil (user primero, luego anon)
            Perfile perfil = null;
            if (incomingUserId.HasValue)
            {
                perfil = await _db.Perfiles.FirstOrDefaultAsync(p => p.UsuarioId == incomingUserId.Value);
            }
            else if (incomingAnonId.HasValue)
            {
                perfil = await _db.Perfiles.FirstOrDefaultAsync(p => p.AnonId == incomingAnonId.Value);
            }

            // Si ya existe perfil completo -> saludar y saltar preguntas
            if (perfil != null && perfil.EstadoPerfilCompleto)
            {
                Guid anonToReturn;
                if (incomingAnonId.HasValue) anonToReturn = incomingAnonId.Value;
                else if (perfil.AnonId.HasValue) anonToReturn = perfil.AnonId.Value;
                else
                {
                    var s = new AnonSession { CreadoEn = DateTime.UtcNow, Estado = "activo" };
                    _db.AnonSessions.Add(s);
                    await _db.SaveChangesAsync();
                    anonToReturn = s.AnonId;
                }

                await LogEventAsync("conversation.started", incomingUserId, anonToReturn, conversationId, "backend", new { note = "profile_exists" });

                return Ok(new
                {
                    conversation_id = conversationId.ToString(),
                    anon_id = anonToReturn.ToString(),
                    messages = new object[]
                    {
                        new { type = "text", text = "¡Hola! Ya tengo algunas respuestas tuyas. ¿Quieres que busque recomendaciones de experiencias ahora?" },
                        new { type = "action", action = "proceed_to_reserva" }
                    }
                });
            }

            // Si no hay perfil completo -> crear/asegurar anon session pero NO enviar las 3 preguntas al chat.
            Guid anonId;
            if (incomingAnonId.HasValue)
            {
                anonId = incomingAnonId.Value;
                var existing = await _db.AnonSessions.FindAsync(anonId);
                if (existing == null)
                {
                    var s = new AnonSession { AnonId = anonId, CreadoEn = DateTime.UtcNow, Estado = "activo" };
                    _db.AnonSessions.Add(s);
                    await _db.SaveChangesAsync();
                }
            }
            else
            {
                var s = new AnonSession { CreadoEn = DateTime.UtcNow, Estado = "activo" };
                _db.AnonSessions.Add(s);
                await _db.SaveChangesAsync();
                anonId = s.AnonId;
            }

            // Opcional: escribir cookie anon_id para que el cliente la reutilice (HttpOnly + seguro)
            try
            {
                var cookieOptions = new Microsoft.AspNetCore.Http.CookieOptions
                {
                    Expires = DateTimeOffset.UtcNow.AddDays(90),
                    HttpOnly = true,
                    Secure = Request.IsHttps,
                    SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Strict,
                    Path = "/"
                };
                Response.Cookies.Append("anon_id", anonId.ToString(), cookieOptions);
            }
            catch
            {
                // No rompemos el flujo si la cookie no se puede setear en algún entorno
            }

            await LogEventAsync("conversation.started", dto?.UserId, anonId, conversationId, "backend", new { locale = dto?.Locale });

            // EN VEZ de retornar las 3 preguntas, devolvemos una acción que instruya al cliente a abrir la pantalla/formulario
            return Ok(new
            {
                conversation_id = conversationId.ToString(),
                anon_id = anonId.ToString(),
                messages = new object[]
                {
                    new { type = "text", text = "Antes de proceder, necesitamos algunas respuestas para recomendarte mejor." },
                    new { type = "action", action = "open_three_questions_form", url = "/Registro/TresPreguntas" }
                }
            });
        }

        // ------------------------
        // POST api/reservas/chat/message
        // ------------------------
        [HttpPost("chat/message")]
        public async Task<IActionResult> ChatMessage([FromBody] ChatMessageDto dto)
        {
            if (dto == null || dto.ConversationId == Guid.Empty) return BadRequest("conversation required");

            Guid? anon = dto.AnonId;
            int? userId = dto.UserId;

            if (!string.IsNullOrEmpty(dto.QKey))
            {
                Perfile perfil = null;
                if (userId.HasValue) perfil = await _db.Perfiles.FirstOrDefaultAsync(p => p.UsuarioId == userId.Value);
                else if (anon.HasValue) perfil = await _db.Perfiles.FirstOrDefaultAsync(p => p.AnonId == anon.Value);

                if (perfil == null)
                {
                    perfil = new Perfile { UsuarioId = userId, AnonId = anon, CreadoEn = DateTime.UtcNow, EstadoPerfilCompleto = false };
                    _db.Perfiles.Add(perfil);
                }

                var qk = dto.QKey.Trim().ToLowerInvariant();
                if (qk == "q1") { perfil.Q1 = dto.QAnswer; }
                else if (qk == "q1_otro") { perfil.Q1_Otro = dto.QAnswer; }
                else if (qk == "q2") { perfil.Q2 = dto.QAnswer; }
                else if (qk == "q3") { perfil.Q3 = dto.QAnswer; }

                if (!string.IsNullOrEmpty(perfil.Q1) && !string.IsNullOrEmpty(perfil.Q2) && !string.IsNullOrEmpty(perfil.Q3)) perfil.EstadoPerfilCompleto = true;
                perfil.ActualizadoEn = DateTime.UtcNow;
                await _db.SaveChangesAsync();

                await LogEventAsync("profile.question.answered", userId, anon, dto.ConversationId, "chatbot-proxy", new { q = dto.QKey, answer = dto.QAnswer });

                if (perfil.EstadoPerfilCompleto)
                {
                    return Ok(new
                    {
                        conversation_id = dto.ConversationId.ToString(),
                        messages = new object[] { new { type = "text", text = "Perfecto, ya tengo tus preferencias. Buscando recomendaciones..." }, new { type = "action", action = "proceed_to_reserva" } }
                    });
                }
                return Ok(new { conversation_id = dto.ConversationId.ToString(), messages = new object[] { new { type = "text", text = "Respuesta registrada." } } });
            }

            // Si el cliente envía reservation_field/reservation_value (compatibilidad con chat.js)
            if (!string.IsNullOrEmpty(dto.ReservationField) && dto.ReservationValue != null)
            {
                // Guardar parcial como evento
                try
                {
                    var payload = new Dictionary<string, object> { { dto.ReservationField, dto.ReservationValue } };
                    await LogEventAsync("reservation.partial", null, dto.AnonId, dto.ConversationId, "chatbot-proxy", payload);
                }
                catch
                {
                    // no bloquear si falla el logging
                }

                // Aquí podrías replicar la lógica de guardado / creación provisional (si lo deseas) — por ahora devolvemos confirmación simple
                return Ok(new { conversation_id = dto.ConversationId.ToString(), messages = new object[] { new { type = "text", text = "Datos de reserva parciales recibidos." } } });
            }

            await LogEventAsync("conversation.message", userId, anon, dto.ConversationId, "chatbot-proxy", new { message = dto.Message });
            // Aquí podrías integrar LLM o reglas para procesar "dto.Message" y devolver respuestas ricas.
            return Ok(new { conversation_id = dto.ConversationId.ToString(), messages = new object[] { new { type = "text", text = "Mensaje recibido." } } });
        }

        // ------------------------
        // POST api/reservas/create  (mantenemos alias "reservas/create" por compatibilidad con frontend existente)
        // ------------------------
        [HttpPost("reservas/create")]
        [HttpPost("create")]
        public async Task<IActionResult> CreateReserva([FromBody] CreateReservaDto dto)
        {
            var callingUserId = await GetCurrentUserIdAsync();
            var isService = IsServiceTokenValid();

            if (callingUserId.HasValue) dto.AnonId = null;

            var exp = await _db.Experiencias.FindAsync(dto.ExperienciaId);
            if (exp == null || !exp.Activa) return BadRequest("Experiencia no encontrada o inactiva");

            int? userForReserva = null;
            if (callingUserId.HasValue) userForReserva = callingUserId;
            else if (isService && dto.UserId.HasValue) userForReserva = dto.UserId;

            // Crear reserva provisional
            var reserva = new Reserva
            {
                UsuarioId = userForReserva,
                AnonId = dto.AnonId,
                ExperienciaId = dto.ExperienciaId,
                NumComensales = dto.NumComensales,
                FechaHora = dto.FechaHora,
                Restricciones = dto.Restricciones,
                NombreReserva = string.IsNullOrWhiteSpace(dto.NombreReserva) ? "Reserva Anónima" : dto.NombreReserva,
                DNI = dto.DNI,
                Telefono = dto.Telefono,
                EsTemporal = true,
                Estado = "pendiente",
                CreadoEn = DateTime.UtcNow
            };

            _db.Reservas.Add(reserva);
            await _db.SaveChangesAsync();

            // Intentar parsear referencia de conversation id (si viene)
            Guid? refConv = null;
            if (!string.IsNullOrWhiteSpace(dto.ReferenciaConversationId))
            {
                if (Guid.TryParse(dto.ReferenciaConversationId, out Guid parsed)) refConv = parsed;
            }

            await LogEventAsync("booking.initiated", reserva.UsuarioId, reserva.AnonId, refConv, isService ? "service" : "backend", new { reserva_id = reserva.Id });

            return Ok(new { reserva_id = reserva.Id, estado = reserva.Estado, es_temporal = reserva.EsTemporal });
        }

        // ------------------------
        // POST api/reservas/confirm
        // ------------------------
        [HttpPost("reservas/confirm")]
        [HttpPost("confirm")]
        public async Task<IActionResult> ConfirmReserva([FromBody] ConfirmReservaDto dto)
        {
            try
            {
                var callingUserId = await GetCurrentUserIdAsync();
                var isService = IsServiceTokenValid();

                // Obtener la reserva SIN hacer Include(...) para evitar materializar columnas NULL de Usuario
                var r = await _db.Reservas.FindAsync(dto.ReservaId);
                if (r == null) return NotFound(new { error = "Reserva no encontrada" });

                // Acción por defecto: confirmar
                var accion = (dto.Accion ?? "confirmar").Trim().ToLowerInvariant();

                if (accion == "confirmar")
                {
                    if (callingUserId.HasValue && !r.UsuarioId.HasValue)
                    {
                        r.UsuarioId = callingUserId.Value;
                    }
                    r.Estado = "confirmada";
                    r.EsTemporal = false;
                    r.ActualizadoEn = DateTime.UtcNow;
                    await _db.SaveChangesAsync();

                    // Guardar preferencias (seguro frente a r.UsuarioId == null)
                    if (dto.GuardarPreferencias)
                    {
                        try
                        {
                            var perfil = await _db.Perfiles
                                .FirstOrDefaultAsync(p =>
                                    (r.UsuarioId.HasValue && p.UsuarioId == r.UsuarioId.Value) ||
                                    (r.AnonId != null && p.AnonId == r.AnonId)
                                );

                            if (perfil != null)
                            {
                                // Determinar el usuario al que atribuimos la preferencia (si existe)
                                int? targetUserId = perfil.UsuarioId ?? r.UsuarioId;
                                if (targetUserId.HasValue)
                                {
                                    var prefObj = new
                                    {
                                        q1 = perfil.Q1 ?? "",
                                        q1_otro = perfil.Q1_Otro ?? "",
                                        q2 = perfil.Q2 ?? "",
                                        q3 = perfil.Q3 ?? ""
                                    };
                                    var prefJson = System.Text.Json.JsonSerializer.Serialize(prefObj);
                                    var pref = new Preferencia
                                    {
                                        UsuarioId = targetUserId.Value,
                                        DatosJson = prefJson,
                                        CreadoEn = DateTime.UtcNow
                                    };
                                    _db.Preferencias.Add(pref);
                                    await _db.SaveChangesAsync();
                                }
                                else
                                {
                                    // No hay usuario asociado; si quieres, guardar prefs en r.Restricciones o ignorar.
                                    // Ejemplo de alternativa: almacenar JSON en Restricciones en vez de Preferencia.
                                }
                            }
                        }
                        catch (Exception exPref)
                        {
                            _logger?.LogError(exPref, "Error guardando preferencias");
                        }
                    }



                    await LogEventAsync("booking.confirmed", r.UsuarioId, r.AnonId, null, isService ? "service" : "backend", new { reserva_id = r.Id });
                    return Ok(new { reserva_id = r.Id, estado = r.Estado });
                }
                else if (accion == "cancelar")
                {
                    r.Estado = "cancelada";
                    r.ActualizadoEn = DateTime.UtcNow;
                    await _db.SaveChangesAsync();
                    await LogEventAsync("booking.cancelled", r.UsuarioId, r.AnonId, null, isService ? "service" : "backend", new { reserva_id = r.Id });
                    return Ok(new { reserva_id = r.Id, estado = r.Estado });
                }

                return BadRequest(new { error = "acción desconocida" });
            }
            catch (Exception ex)
            {
                // Registrar en consola (o _logger si lo tienes). Devolver 500 con detalle (útil en desarrollo).
                try { Console.Error.WriteLine(ex.ToString()); } catch { }
                return StatusCode(500, new { error = "Error interno al confirmar la reserva.", detail = ex.Message });
            }
        }



        // ------------------------
        // POST api/reservas/merge_profile
        // ------------------------
        [HttpPost("merge_profile")]
        public async Task<IActionResult> MergeProfile([FromBody] MergeProfileDto dto)
        {
            var isService = IsServiceTokenValid();
            var callerUser = GetCurrentUserId();
            if (!isService && !callerUser.HasValue) return Unauthorized();

            var user = await _db.Usuarios.FindAsync(dto.UserId);
            if (user == null) return NotFound("user not found");

            var anon = await _db.AnonSessions.FindAsync(dto.AnonId);
            if (anon == null) return NotFound("anon session not found");

            if (dto.TransferProfile)
            {
                var perfilAnon = await _db.Perfiles.FirstOrDefaultAsync(p => p.AnonId == dto.AnonId);
                if (perfilAnon != null)
                {
                    perfilAnon.UsuarioId = dto.UserId;
                    perfilAnon.AnonId = null;
                    perfilAnon.ActualizadoEn = DateTime.UtcNow;
                    await _db.SaveChangesAsync();
                }
            }

            if (dto.TransferEvents)
            {
                var eventos = await _db.Eventos.Where(e => e.AnonId == dto.AnonId).ToListAsync();
                foreach (var ev in eventos) ev.UsuarioId = dto.UserId;
                await _db.SaveChangesAsync();
            }

            var reservasAnon = await _db.Reservas.Where(r => r.AnonId == dto.AnonId).ToListAsync();
            foreach (var ra in reservasAnon)
            {
                ra.UsuarioId = dto.UserId;
                ra.AnonId = null;
                ra.ActualizadoEn = DateTime.UtcNow;
            }
            await _db.SaveChangesAsync();

            anon.Estado = "merged";
            await _db.SaveChangesAsync();

            await LogEventAsync("profile.merged", dto.UserId, dto.AnonId, null, "backend", new { merged = true });

            return Ok(new { merged = true, user_id = dto.UserId, transferred_reservas = reservasAnon.Count });
        }

        // ------------------------
        // POST api/reservas/events
        // Endpoint abierto para servicios si presentan service-token
        // ------------------------
        [HttpPost("events")]
        public async Task<IActionResult> ReceiveEvent([FromBody] EventDto dto)
        {
            if (!IsServiceTokenValid() && GetCurrentUserId() == null) return Unauthorized();

            // Guardar como evento genérico (mapeo ahora correcto de UsuarioId -> int?)
            var evt = new Evento
            {
                EventType = dto.EventType ?? "unknown",
                UsuarioId = dto.UsuarioId,
                AnonId = dto.AnonId,
                ConversationId = dto.ConversationId,
                SenderId = dto.SenderId ?? "external",
                Payload = dto.Payload == null ? null : System.Text.Json.JsonSerializer.Serialize(dto.Payload),
                CreadoEn = DateTime.UtcNow
            };
            _db.Eventos.Add(evt);
            await _db.SaveChangesAsync();
            return Ok(new { accepted = true, id = evt.EventoId });
        }

        // ------------------------
        // POST api/reservas/score/rules
        // Servicio simple de ranking por reglas (útil para MVP)
        // ------------------------
        [HttpPost("score/rules")]
        public async Task<IActionResult> ScoreRules([FromBody] ScoreRequestDto req)
        {
            if (!IsServiceTokenValid() && GetCurrentUserId() == null) return Unauthorized();

            // Obtener perfil si existe (user o anon)
            Perfile perfil = null;
            if (req.UserId.HasValue) perfil = await _db.Perfiles.FirstOrDefaultAsync(p => p.UsuarioId == req.UserId.Value);
            else if (req.AnonId.HasValue) perfil = await _db.Perfiles.FirstOrDefaultAsync(p => p.AnonId == req.AnonId.Value);

            // Obtener candidatos: si vienen en request candidatos -> usarlos; si no, tomar Experiencias activas
            List<Experiencia> candidatos;
            if (req.Candidates != null && req.Candidates.Count > 0)
            {
                var ids = req.Candidates.Select(c =>
                {
                    if (c.ContainsKey("id") && int.TryParse(c["id"]?.ToString(), out int v)) return v;
                    return 0;
                }).Where(i => i > 0).ToList();
                candidatos = await _db.Experiencias.Where(e => ids.Contains(e.Id) && e.Activa).ToListAsync();
            }
            else
            {
                candidatos = await _db.Experiencias.Where(e => e.Activa).ToListAsync();
            }

            Func<Experiencia, double> scoreFn = e =>
            {
                double s = 0.1;
                if (perfil != null && !string.IsNullOrWhiteSpace(perfil.Q3))
                {
                    var q3 = perfil.Q3.ToLowerInvariant();
                    var nameDesc = ((e.Nombre ?? "") + " " + (e.Descripcion ?? "")).ToLowerInvariant();
                    if (q3.Contains("veget")) { if (nameDesc.Contains("veget")) s += 0.7; }
                    if (q3.Contains("gourmet") || q3.Contains("alta")) { if (nameDesc.Contains("degust") || nameDesc.Contains("gourmet")) s += 0.7; }
                    if (q3.Contains("tradicional") || q3.Contains("criolla")) { if (nameDesc.Contains("trad") || nameDesc.Contains("crioll")) s += 0.7; }
                }
                s += Math.Max(0, 0.1 - ((double)e.Precio / 1000.0));
                return s;
            };

            var scored = candidatos.Select(e => new ScoreItem { Id = e.Id, Score = Math.Round(scoreFn(e), 4) })
                                   .OrderByDescending(x => x.Score)
                                   .Take(req.TopK)
                                   .ToList();

            var resp = new ScoreResponseDto { RequestId = req.RequestId ?? Guid.NewGuid().ToString(), ModelVersion = "rules-v1", Scores = scored, LatencyMs = 0 };
            await LogEventAsync("recommendation.requested", null, req.AnonId, null, "score-rules", new { request = req.RequestId, topk = req.TopK, ranked = resp.Ranked });
            return Ok(resp);
        }

        // ------------------------
        // GET api/reservas/perfil/anon/{anonId}
        // ------------------------
        [HttpGet("perfil/anon/{anonId}")]
        public async Task<IActionResult> GetPerfilByAnon(Guid anonId)
        {
            var perfil = await _db.Perfiles.FirstOrDefaultAsync(p => p.AnonId == anonId);
            if (perfil == null) return NotFound();
            return Ok(perfil);
        }

        // ------------------------
        // GET api/reservas/perfil/user/{userId}
        // ------------------------
        [HttpGet("perfil/user/{userId}")]
        public async Task<IActionResult> GetPerfilByUser(int userId)
        {
            var perfil = await _db.Perfiles.FirstOrDefaultAsync(p => p.UsuarioId == userId);
            if (perfil == null) return NotFound();
            return Ok(perfil);
        }
    }
}
