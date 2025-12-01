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
    }
}
