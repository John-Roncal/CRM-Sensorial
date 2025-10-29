// Controllers/ChatController.cs
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AppWebCentralRestaurante.Data;
using AppWebCentralRestaurante.Models;
using AppWebCentralRestaurante.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Security.Claims;

namespace AppWebCentralRestaurante.Controllers
{
    public class ChatController : Controller
    {
        private readonly ICohereService _cohere;
        private readonly CentralContext _context;
        private readonly ILogger<ChatController> _logger;
        private readonly RecommendationService _recSvc;

        // Keys de sesión
        private const string SessionKeyConversation = "ConversationMessages";
        private const string SessionKeyDraft = "ReservationDraft";
        private const string SessionKeyPendingReservation = "PendingReservationToSave";

        public ChatController(
            ICohereService cohere,
            CentralContext context,
            ILogger<ChatController> logger,
            RecommendationService recSvc)
        {
            _cohere = cohere ?? throw new ArgumentNullException(nameof(cohere));
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _recSvc = recSvc ?? throw new ArgumentNullException(nameof(recSvc));
        }

        // GET: /Chat
        [HttpGet]
        public async Task<IActionResult> Index()
        {
            var convHistory = GetConversationFromSession() ?? new List<string>();
            var draft = GetDraftFromSession();

            // Precargar preferencias usuario autenticado si no hay draft
            if (draft == null && User?.Identity?.IsAuthenticated == true)
            {
                try
                {
                    var claim = User.FindFirst(ClaimTypes.NameIdentifier);
                    if (claim != null && int.TryParse(claim.Value, out var uid))
                    {
                        var pref = await _context.Preferencias.AsNoTracking().FirstOrDefaultAsync(p => p.UsuarioId == uid);
                        if (pref != null && !string.IsNullOrWhiteSpace(pref.DatosJson))
                        {
                            try
                            {
                                var doc = JsonSerializer.Deserialize<JsonElement>(pref.DatosJson);
                                var pre = new ReservationDraft();
                                if (doc.TryGetProperty("Personas", out var pEl) && pEl.TryGetInt32(out var pVal)) pre.Personas = pVal;
                                if (doc.TryGetProperty("ExperienciaId", out var eEl) && eEl.TryGetInt32(out var eVal)) pre.ExperienciaId = eVal;
                                if (doc.TryGetProperty("Restricciones", out var rEl) && rEl.ValueKind == JsonValueKind.String) pre.Restricciones = rEl.GetString();
                                if (doc.TryGetProperty("NombreUsuario", out var nEl) && nEl.ValueKind == JsonValueKind.String) pre.NombreUsuario = nEl.GetString();
                                pre.FromThreeQuestions = false;
                                pre.Step = NextStepFromDraft(pre);
                                draft = pre;
                                SaveDraftToSession(draft);
                            }
                            catch (Exception exInner)
                            {
                                _logger.LogWarning(exInner, "No se pudo parsear Preferencias.DatosJson para usuario {u}", claim.Value);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error al intentar precargar preferencias del usuario en Chat/Index");
                }
            }

            // Mensaje inicial incluyendo info del draft si existe
            if ((convHistory ?? new List<string>()).Count == 0)
            {
                string initial;
                if (draft != null && (draft.FromThreeQuestions || draft.Personas > 0 || draft.ExperienciaId.HasValue || !string.IsNullOrWhiteSpace(draft.Restricciones)))
                {
                    initial = $"¡Hola {(!string.IsNullOrWhiteSpace(draft.NombreUsuario) ? draft.NombreUsuario : "amigo")}! " +
                              "Ya tengo algunos detalles: " +
                              $"{(draft.Personas > 0 ? $"{draft.Personas} comensales" : "")} " +
                              $"{(draft.ExperienciaId.HasValue ? $"(experiencia #{draft.ExperienciaId})" : "")} " +
                              $"{(!string.IsNullOrWhiteSpace(draft.Restricciones) ? $", restricciones: {draft.Restricciones}" : "")}. " +
                              "¿Deseas que te ayude a reservar ahora? (Responde 'sí' para continuar o 'no' para volver al perfil).";
                }
                else
                {
                    initial = "¡Hola! Soy el asistente de Central. ¿Qué experiencia te interesa o para qué día quieres reservar?";
                }

                var convToSave = new List<string> { "Bot: " + initial };
                SaveConversationToSession(convToSave);
                ViewData["InitialBot"] = initial;
            }
            else
            {
                var convList = GetConversationFromSession() ?? new List<string>();
                ViewData["InitialBot"] = convList.Last().Replace("Bot: ", "");
            }

            ViewData["Draft"] = draft;
            return View();
        }

        // POST: /Chat/SendMessage
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SendMessage([FromForm] string userText, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(userText))
                return BadRequest(new { error = "Texto vacío" });

            try
            {
                var conv = GetConversationFromSession() ?? new List<string>();
                conv.Add("Usuario: " + userText);

                const int maxLinesToKeep = 40; // guardamos más contexto si conviene
                if (conv.Count > maxLinesToKeep)
                    conv = conv.Skip(Math.Max(0, conv.Count - maxLinesToKeep)).ToList();

                var conversationSoFar = string.Join("\n", conv);
                var draft = GetDraftFromSession() ?? new ReservationDraft();

                // Construir prefijo con info conocida (si existe) para ayudar a la IA
                string promptPrefix = "";
                if (draft != null)
                {
                    var parts = new List<string>();
                    if (!string.IsNullOrWhiteSpace(draft.NombreUsuario)) parts.Add($"NombreUsuario: {draft.NombreUsuario}");
                    if (draft.Personas > 0) parts.Add($"Personas: {draft.Personas}");
                    if (draft.ExperienciaId.HasValue) parts.Add($"ExperienciaId: {draft.ExperienciaId}");
                    if (!string.IsNullOrWhiteSpace(draft.Restricciones)) parts.Add($"Restricciones: {draft.Restricciones}");
                    if (!string.IsNullOrWhiteSpace(draft.PreferenciasJson)) parts.Add($"Preferencias: {draft.PreferenciasJson}");
                    if (parts.Count > 0)
                    {
                        promptPrefix = "INFORMACION_INICIAL: " + string.Join(" ; ", parts) + "\n\n";
                    }
                }

                var conversationForAI = promptPrefix + conversationSoFar;

                // Llamada a servicio IA
                var result = await _cohere.SendConversationAndExtractAsync(conversationForAI, userText, cancellationToken);

                var botReply = string.IsNullOrWhiteSpace(result?.BotReply) ? "[Sin respuesta]" : result.BotReply.Trim();

                conv.Add("Bot: " + botReply);
                SaveConversationToSession(conv);

                // ---------- Primero: extraer datos estructurados que no sean preferencias ----------
                if (draft == null) draft = new ReservationDraft();

                if (!string.IsNullOrWhiteSpace(result?.Dia)) draft.Dia = result.Dia.Trim();
                if (!string.IsNullOrWhiteSpace(result?.Hora)) draft.Hora = result.Hora.Trim();
                if (result?.Personas is int p && p > 0) draft.Personas = p;
                if (!string.IsNullOrWhiteSpace(result?.ExperienciaCode) && int.TryParse(result.ExperienciaCode, out var codeInt)) draft.ExperienciaId = codeInt;
                if (result?.ExperienciaId is int eId && eId > 0) draft.ExperienciaId = eId;

                // Detectar si el usuario en este mensaje parece estar respondiendo preferencias (palabras clave)
                var lowerUser = NormalizeForMatching(userText);
                bool looksLikePreferences = Regex.IsMatch(lowerUser, @"\b(prefe|preferencias|me gusta|me gustaria|me gustar|me encanta|odio|preferir)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

                // Si el bot preguntó por restricciones explícitamente y el usuario dice "no" -> marcar sin restricciones
                bool userSaysNoRestrictions = Regex.IsMatch(lowerUser, @"\b(no|ninguna|ninguno|ningun|ningunas|sin|no tengo)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

                // ---------- RESTRICCIONES: sólo procesar como restricciones si el texto no parece prefs ----------
                // EXTRAER RESTRICCIONES ÚNICAMENTE desde el mensaje del USUARIO (no desde la respuesta de la IA)
                var restrFromUser = TryExtractRestriccionesFromText(userText);
                if (!string.IsNullOrWhiteSpace(restrFromUser))
                {
                    draft.Restricciones = MergeRestrictions(draft.Restricciones, restrFromUser);
                }
                else
                {
                    // Si el bot preguntó "¿Tienes restricciones?" y el usuario respondió "no", dejar null
                    if (userSaysNoRestrictions)
                    {
                        // sólo marcar como "ninguna" si el paso actual era preguntar restricciones
                        if (NextStepFromDraft(draft) == "ask_restricciones")
                            draft.Restricciones = null;
                    }
                }

                // ---------- CONTACTO: preferir campos estructurados devueltos por la IA ----------
                if (!string.IsNullOrWhiteSpace(result?.ClienteNombre))
                {
                    draft.NombreUsuario = string.IsNullOrWhiteSpace(draft.NombreUsuario) ? result.ClienteNombre.Trim() : draft.NombreUsuario;
                }

                if (!string.IsNullOrWhiteSpace(result?.ClienteDni))
                {
                    draft.Dni = string.IsNullOrWhiteSpace(draft.Dni) ? result.ClienteDni.Trim() : draft.Dni;
                }

                if (!string.IsNullOrWhiteSpace(result?.ClienteTelefono))
                {
                    draft.Telefono = string.IsNullOrWhiteSpace(draft.Telefono) ? result.ClienteTelefono.Trim() : draft.Telefono;
                }

                // fallback heurístico desde el texto del usuario
                var contactFromUser = TryExtractContactInfoFromText(userText);
                if (contactFromUser != null)
                {
                    if (string.IsNullOrWhiteSpace(draft.NombreUsuario) && !string.IsNullOrWhiteSpace(contactFromUser.Value.Name))
                        draft.NombreUsuario = contactFromUser.Value.Name;
                    if (string.IsNullOrWhiteSpace(draft.Dni) && !string.IsNullOrWhiteSpace(contactFromUser.Value.Dni))
                        draft.Dni = contactFromUser.Value.Dni;
                    if (string.IsNullOrWhiteSpace(draft.Telefono) && !string.IsNullOrWhiteSpace(contactFromUser.Value.Telefono))
                        draft.Telefono = contactFromUser.Value.Telefono;
                }

                // rellenar nombre desde claim si es posible y aún no existe
                if (string.IsNullOrWhiteSpace(draft.NombreUsuario) && User?.Identity?.IsAuthenticated == true)
                {
                    draft.NombreUsuario = User.Identity.Name;
                }

                // ---------- PREFERENCIAS: si el bot pedía 'ask_prefs' tratamos el mensaje como prefs; si no, intentamos detectar prefs ----------
                // ---------- PREFERENCIAS: si el bot pedía 'ask_prefs' tratamos el mensaje como prefs; si no, intentamos detectar prefs ----------
                var currentStepBefore = NextStepFromDraft(draft);

                // Si el bot explícitamente estaba pidiendo preferencias, guardar lo que diga el usuario.
                if (currentStepBefore == "ask_prefs")
                {
                    if (!string.IsNullOrWhiteSpace(userText))
                    {
                        var prefsObj = new { Texto = userText.Trim(), CapturadoEn = DateTime.UtcNow, Source = "user" };
                        draft.PreferenciasJson = JsonSerializer.Serialize(prefsObj);
                    }
                }
                else
                {
                    // Intentar detectar preferencias AUTOMÁTICAMENTE pero **solo** desde lo que escribió el USUARIO primero.
                    var prefsDetected = TryExtractPreferencesFromText(userText);

                    // Si no lo detectamos en el texto del usuario, entonces podemos intentar revisar la respuesta "raw" de la IA,
                    // pero sólo si esa respuesta contiene palabras clave que claramente indican preferencias (evita capturar lo que el bot SUGIRIÓ).
                    if (string.IsNullOrWhiteSpace(prefsDetected) && !string.IsNullOrWhiteSpace(result?.RawResponse))
                    {
                        var rawNorm = NormalizeForMatching(result.RawResponse);
                        if (Regex.IsMatch(rawNorm, @"\b(me gusta|prefe|me encant|mi prefer|prefiero|me gustar|odio)\b"))
                        {
                            prefsDetected = TryExtractPreferencesFromText(result.RawResponse);
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(prefsDetected) && string.IsNullOrWhiteSpace(draft.PreferenciasJson))
                    {
                        var prefsObj = new { Texto = prefsDetected, Detected = true, CapturadoEn = DateTime.UtcNow };
                        draft.PreferenciasJson = JsonSerializer.Serialize(prefsObj);
                    }
                }


                // paso siguiente (ahora NextStepFromDraft incluye contacto y preferencias)
                draft.Step = NextStepFromDraft(draft);

                SaveDraftToSession(draft);

                // Si el draft está completo (done) construimos resumen (no guardamos aún)
                if (draft.Step == "done")
                {
                    int experienciaToUse = -1;
                    string nombreExp = "No especificada";
                    decimal precioUnit = 0m;
                    if (draft.ExperienciaId.HasValue)
                    {
                        experienciaToUse = draft.ExperienciaId.Value;
                    }
                    else
                    {
                        try
                        {
                            var input = new RecommendationService.RecommendationInput
                            {
                                NumComensales = draft.Personas > 0 ? draft.Personas : 1,
                                Restricciones = draft.Restricciones ?? ""
                            };
                            var (predId, scores, labels) = _recSvc.Predict(input);
                            if (predId > 0) experienciaToUse = predId;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "RecommendationService falló en SendMessage (solo sugerencia).");
                        }
                    }

                    if (experienciaToUse > 0)
                    {
                        var exp = await _context.Experiencias.AsNoTracking().FirstOrDefaultAsync(e => e.Id == experienciaToUse, cancellationToken);
                        if (exp != null)
                        {
                            nombreExp = exp.Nombre ?? experienciaToUse.ToString();
                            precioUnit = exp.Precio;
                        }
                    }

                    var fechaHoraParsed = ParseFechaHora(draft.Dia, draft.Hora);
                    string fechaHoraText = fechaHoraParsed == DateTime.MinValue ? $"{draft.Dia} {draft.Hora}".Trim() : fechaHoraParsed.ToString("yyyy-MM-dd HH:mm");

                    var total = precioUnit * (draft.Personas > 0 ? draft.Personas : 1);

                    // asegurar nombre a mostrar (usar claim si draft.NombreUsuario está vacío)
                    string displayName = draft.NombreUsuario;
                    if (string.IsNullOrWhiteSpace(displayName) && User?.Identity?.IsAuthenticated == true)
                    {
                        displayName = User.Identity.Name;
                    }
                    if (string.IsNullOrWhiteSpace(displayName)) displayName = "No especificado";

                    // Formatear precios explícitamente para es-PE
                    var unitPriceText = precioUnit.ToString("C", new CultureInfo("es-PE"));
                    var totalText = total.ToString("C", new CultureInfo("es-PE"));

                    // Construcción amigable del resumen.
                    var resumenText = $"Resumen de reserva (no guardada aún):\n" +
                                      $"Nombre a nombre: {displayName}\n" +
                                      $"DNI: {draft.Dni ?? "—"}\n" +
                                      $"Teléfono: {draft.Telefono ?? "—"}\n" +
                                      $"Experiencia: {nombreExp}\n" +
                                      $"Día y hora: {fechaHoraText}\n" +
                                      $"Personas: {draft.Personas}\n" +
                                      $"Restricciones: {(string.IsNullOrWhiteSpace(draft.Restricciones) ? "Ninguna" : draft.Restricciones)}\n" +
                                      $"Preferencias: {(string.IsNullOrWhiteSpace(draft.PreferenciasJson) ? "—" : "[guardadas]")}\n" +
                                      $"Precio por persona: {unitPriceText}\n" +
                                      $"Total: {totalText}\n\n" +
                                      $"¿Tienes alguna duda sobre las experiencias o sobre tu reserva? Si quieres, puedo darte más detalles de la experiencia seleccionada.";

                    var actions = new List<object>
                    {
                        new { label = "Confirmar y guardar reserva", action = "confirm" },
                        new { label = "Editar detalles", action = "edit" },
                        new { label = "Más info de la experiencia", action = "info_experiencia" }
                    };

                    if (!string.IsNullOrWhiteSpace(draft.Restricciones) || !string.IsNullOrWhiteSpace(draft.PreferenciasJson))
                        actions.Add(new { label = "Guardar estas preferencias", action = "save_prefs" });

                    // NOTE: no concatenamos el resumen dentro de `bot` para evitar duplicados en el frontend.
                    return Json(new
                    {
                        bot = botReply, // mostramos solo la respuesta natural del bot
                        draft = new
                        {
                            step = draft.Step,
                            experienciaId = draft.ExperienciaId,
                            personas = draft.Personas,
                            dia = draft.Dia,
                            hora = draft.Hora,
                            restricciones = draft.Restricciones,
                            nombreUsuario = draft.NombreUsuario,
                            dni = draft.Dni,
                            telefono = draft.Telefono,
                            preferenciasJson = draft.PreferenciasJson,
                            fromThreeQuestions = draft.FromThreeQuestions
                        },
                        done = true,
                        summary = new
                        {
                            text = resumenText,
                            experienciaId = experienciaToUse,
                            experienciaNombre = nombreExp,
                            fechaHora = fechaHoraText,
                            total = total,
                            unitPrice = precioUnit,
                            unitPriceText = unitPriceText,
                            totalText = totalText
                        },
                        actions = actions
                    });
                }


                // respuesta normal con estado draft
                return Json(new
                {
                    bot = botReply,
                    draft = new
                    {
                        step = draft.Step,
                        experienciaId = draft.ExperienciaId,
                        personas = draft.Personas,
                        dia = draft.Dia,
                        hora = draft.Hora,
                        restricciones = draft.Restricciones,
                        nombreUsuario = draft.NombreUsuario,
                        dni = draft.Dni,
                        telefono = draft.Telefono,
                        preferenciasJson = draft.PreferenciasJson,
                        fromThreeQuestions = draft.FromThreeQuestions
                    },
                    done = false
                });
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Petición cancelada por el cliente.");
                return StatusCode(499, new { bot = "La petición fue cancelada." });
            }
            catch (HttpRequestException httpEx)
            {
                _logger.LogError(httpEx, "Error HTTP al llamar al servicio de IA.");
                return StatusCode(502, new { bot = "Error contactando al servicio de IA. Intenta de nuevo más tarde." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error inesperado en SendMessage");
                return StatusCode(500, new { bot = "Error interno del servidor. Revisa los logs." });
            }
        }



        // POST: /Chat/ConfirmReservation
        // Guarda la reserva final basándose en el draft guardado en sesión.
        // Si usuario no autenticado -> guarda pending en Session y devuelve needLogin = true (pero NO la guarda automáticamente)
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ConfirmReservation(CancellationToken cancellationToken)
        {
            var draft = GetDraftFromSession();
            if (draft == null) return BadRequest(new { ok = false, message = "No hay datos de reserva en sesión." });

            if (draft.Personas <= 0 || (!draft.ExperienciaId.HasValue))
                return BadRequest(new { ok = false, message = "La reserva no está completa." });

            // Si no autenticado -> guardar pending en session y pedir login/registro
            if (User?.Identity?.IsAuthenticated != true)
            {
                HttpContext.Session.SetString(SessionKeyPendingReservation, JsonSerializer.Serialize(draft));
                var redirectTo = Url.Action("Index", "Registro");
                // No guardamos la reserva; sólo avisamos que el usuario debe autenticarse y conservar el pending
                return Json(new { ok = false, needLogin = true, redirect = redirectTo, message = "Necesitas iniciar sesión o registrarte para confirmar la reserva." });
            }

            var claim = User.FindFirst(ClaimTypes.NameIdentifier);
            if (claim == null || !int.TryParse(claim.Value, out var usuarioId))
                return Unauthorized(new { ok = false, message = "Claim inválido." });

            var experiencia = await _context.Experiencias.FirstOrDefaultAsync(e => e.Id == draft.ExperienciaId, cancellationToken);
            if (experiencia == null) return BadRequest(new { ok = false, message = "La experiencia seleccionada no existe." });

            var fechaHora = ParseFechaHora(draft.Dia, draft.Hora);
            if (fechaHora == DateTime.MinValue)
            {
                // usamos UtcNow para persistir, pero convertimos a local por legibilidad
                fechaHora = DateTime.UtcNow.AddDays(1).Date.AddHours(19);
            }

            string nombreReserva = !string.IsNullOrWhiteSpace(draft.NombreUsuario)
                ? $"Reserva de {draft.NombreUsuario}"
                : $"Reserva {fechaHora:yyyy-MM-dd HH:mm}";

            if (!string.IsNullOrWhiteSpace(draft.Dni)) nombreReserva += $" | DNI: {draft.Dni}";
            if (!string.IsNullOrWhiteSpace(draft.Telefono)) nombreReserva += $" | Tel: {draft.Telefono}";

            var reserva = new Reserva
            {
                UsuarioId = usuarioId,
                NombreReserva = nombreReserva,
                NumComensales = draft.Personas > 0 ? draft.Personas : 1,
                ExperienciaId = experiencia.Id,
                Restricciones = draft.Restricciones,
                FechaHora = fechaHora,
                Estado = "pendiente",
                CreadoEn = DateTime.UtcNow
            };

            try
            {
                _context.Reservas.Add(reserva);
                await _context.SaveChangesAsync(cancellationToken);
            }
            catch (Exception dbEx)
            {
                _logger.LogError(dbEx, "Error guardando reserva (ConfirmReservation)");
                return StatusCode(500, new { ok = false, message = "Error al guardar la reserva." });
            }

            // Intentar guardar log de recomendación (no crítico)
            try
            {
                var log = new RecomendacionLog
                {
                    UsuarioId = usuarioId,
                    ReservaId = reserva.Id,
                    ExperienciaId = reserva.ExperienciaId,
                    Score = null,
                    CaracteristicasJson = JsonSerializer.Serialize(new { Draft = draft }),
                    CreadoEn = DateTime.UtcNow
                };
                _context.RecomendacionesLog.Add(log);
                await _context.SaveChangesAsync(cancellationToken);
            }
            catch (Exception exLog)
            {
                _logger.LogWarning(exLog, "No se pudo guardar RecomendacionLog (no crítico).");
            }

            // limpiar session (draft y conversation y pending)
            HttpContext.Session.Remove(SessionKeyDraft);
            HttpContext.Session.Remove(SessionKeyConversation);
            HttpContext.Session.Remove(SessionKeyPendingReservation);

            var total = experiencia.Precio * reserva.NumComensales;
            var confirmText = $"Reserva confirmada ✅\nID: {reserva.Id}\nExperiencia: {experiencia.Nombre}\nDía y hora: {reserva.FechaHora:yyyy-MM-dd HH:mm}\nPersonas: {reserva.NumComensales}\nRestricciones: {reserva.Restricciones ?? "Ninguna"}\nTotal: {total:C}";

            // agregar mensaje de confirmación al historial de conversación
            var conv = GetConversationFromSession() ?? new List<string>();
            conv.Add("Bot: " + confirmText);
            SaveConversationToSession(conv);

            return Json(new { ok = true, reservaId = reserva.Id, message = confirmText, total });
        }

        // GET: /Chat/ConfirmPendingAfterLogin
        // Nuevo comportamiento: si confirm=true -> guarda; si confirm=false|ausente -> devuelve el draft pendiente para que el frontend pida confirmación.
        [HttpGet]
        public async Task<IActionResult> ConfirmPendingAfterLogin(bool confirm = false, CancellationToken cancellationToken = default)
        {
            if (User?.Identity?.IsAuthenticated != true) return Unauthorized();

            var s = HttpContext.Session.GetString(SessionKeyPendingReservation);
            if (string.IsNullOrWhiteSpace(s)) return Json(new { ok = false, message = "No hay reserva pendiente." });

            try
            {
                var draft = JsonSerializer.Deserialize<ReservationDraft>(s);
                if (draft == null) return Json(new { ok = false, message = "Draft inválido." });

                // Si no se solicita confirmación explícita devolvemos el draft para que el cliente muestre el resumen y pida confirmación al usuario
                if (!confirm)
                {
                    return Json(new { ok = true, pending = true, draft });
                }

                // confirm == true -> guardamos la reserva con el usuario autenticado
                var claim = User.FindFirst(ClaimTypes.NameIdentifier);
                if (claim == null || !int.TryParse(claim.Value, out var usuarioId))
                    return Unauthorized(new { ok = false, message = "Claim inválido." });

                var experiencia = await _context.Experiencias.FirstOrDefaultAsync(e => e.Id == draft.ExperienciaId, cancellationToken);
                if (experiencia == null) return Json(new { ok = false, message = "La experiencia seleccionada no existe." });

                var fechaHora = ParseFechaHora(draft.Dia, draft.Hora);
                if (fechaHora == DateTime.MinValue) fechaHora = DateTime.UtcNow.AddDays(1).Date.AddHours(19);

                var reserva = new Reserva
                {
                    UsuarioId = usuarioId,
                    NombreReserva = !string.IsNullOrWhiteSpace(draft.NombreUsuario) ? $"Reserva de {draft.NombreUsuario}" : $"Reserva {fechaHora:yyyy-MM-dd HH:mm}",
                    NumComensales = draft.Personas > 0 ? draft.Personas : 1,
                    ExperienciaId = experiencia.Id,
                    Restricciones = draft.Restricciones,
                    FechaHora = fechaHora,
                    Estado = "pendiente",
                    CreadoEn = DateTime.UtcNow
                };

                _context.Reservas.Add(reserva);
                await _context.SaveChangesAsync(cancellationToken);

                // limpiar pending
                HttpContext.Session.Remove(SessionKeyPendingReservation);
                HttpContext.Session.Remove(SessionKeyDraft);
                HttpContext.Session.Remove(SessionKeyConversation);

                return Json(new { ok = true, reservaId = reserva.Id, message = "Reserva guardada tras iniciar sesión." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error confirmando pending after login");
                return StatusCode(500, new { ok = false, message = "Error guardando reserva pendiente." });
            }
        }

        // GET: /Chat/GetAvailableSlots
        [HttpGet]
        public IActionResult GetAvailableSlots(int days = 7)
        {
            // usar hora local consistente (derivada de UTC)
            var now = DateTime.UtcNow.ToLocalTime();
            var slots = new List<object>();
            var hours = new[] { 18, 19, 20 };
            for (int d = 0; d < Math.Min(days, 14); d++)
            {
                var day = now.Date.AddDays(d);
                foreach (var h in hours)
                {
                    var dt = day.AddHours(h);
                    if (dt > now)
                        slots.Add(new { datetime = dt.ToString("yyyy-MM-dd HH:mm"), display = dt.ToString("dddd, dd MMM yyyy HH:mm", new CultureInfo("es-PE")) });
                }
            }
            return Json(new { slots });
        }

        // POST: /Chat/GuardarPreferenciasDesdeDraft
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> GuardarPreferenciasDesdeDraft(CancellationToken cancellationToken)
        {
            var draft = GetDraftFromSession();
            if (draft == null)
                return BadRequest(new { ok = false, message = "No hay draft en sesión." });

            // Normalizar restricciones
            var normalizedRestr = MergeRestrictions(null, draft.Restricciones);

            var datos = new
            {
                Personas = draft.Personas,
                ExperienciaId = draft.ExperienciaId,
                Restricciones = normalizedRestr,
                Preferencias = draft.PreferenciasJson,    // <-- campo nuevo: guarda el JSON de preferencias (string)
                NombreUsuario = draft.NombreUsuario,
                FromThreeQuestions = draft.FromThreeQuestions,
                Timestamp = DateTime.UtcNow
            };

            var datosJson = JsonSerializer.Serialize(datos);

            if (User?.Identity?.IsAuthenticated != true)
            {
                // Guardar en sesión temporalmente y pedir login/registro
                HttpContext.Session.SetString("PendingPreferencesJson", datosJson);
                var redirectTo = Url.Action("Index", "Registro");
                return Json(new { ok = false, needLogin = true, redirect = redirectTo, message = "Necesitas iniciar sesión para guardar preferencias. Te redirigimos." });
            }

            // Si está autenticado -> guardar en DB o actualizar
            var claim = User.FindFirst(ClaimTypes.NameIdentifier);
            if (claim == null || !int.TryParse(claim.Value, out var usuarioId))
                return Unauthorized(new { ok = false, message = "Claim inválido." });

            try
            {
                var pref = await _context.Preferencias.FirstOrDefaultAsync(p => p.UsuarioId == usuarioId, cancellationToken);
                if (pref == null)
                {
                    pref = new Preferencia
                    {
                        UsuarioId = usuarioId,
                        DatosJson = datosJson,
                        CreadoEn = DateTime.UtcNow
                    };
                    _context.Preferencias.Add(pref);
                }
                else
                {
                    pref.DatosJson = datosJson;
                    pref.ActualizadoEn = DateTime.UtcNow;
                    _context.Preferencias.Update(pref);
                }
                await _context.SaveChangesAsync(cancellationToken);

                // reflejar en draft
                draft.PreferenciasJson = datosJson;
                SaveDraftToSession(draft);

                return Ok(new { ok = true, message = "Preferencias guardadas." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error guardando preferencias");
                return StatusCode(500, new { ok = false, message = "Error guardando preferencias." });
            }
        }


        // POST: /Chat/ClearDraft
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult ClearDraft()
        {
            HttpContext.Session.Remove(SessionKeyDraft);
            HttpContext.Session.Remove(SessionKeyConversation);
            HttpContext.Session.Remove(SessionKeyPendingReservation);
            return Ok(new { cleared = true });
        }

        // GET: /Chat/VerResumen/5
        [HttpGet]
        public async Task<IActionResult> VerResumen(int id, CancellationToken cancellationToken)
        {
            if (id <= 0) return BadRequest();

            var reserva = await _context.Reservas
                .AsNoTracking()
                .Include(r => r.Experiencia)
                .Include(r => r.Usuario)
                .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

            if (reserva == null) return NotFound();

            return View(reserva);
        }

        #region Sesión / Draft helpers

        private List<string>? GetConversationFromSession()
        {
            var s = HttpContext.Session.GetString(SessionKeyConversation);
            if (string.IsNullOrEmpty(s)) return null;
            try { return JsonSerializer.Deserialize<List<string>>(s); }
            catch
            {
                _logger.LogWarning("Conversación en sesión con formato inválido; se reinicia.");
                HttpContext.Session.Remove(SessionKeyConversation);
                return null;
            }
        }

        private void SaveConversationToSession(List<string> conv)
        {
            try
            {
                // limitar tamaño serializado para evitar exceder stores basados en cookies
                if (conv != null && conv.Count > 200)
                {
                    conv = conv.Skip(conv.Count - 200).ToList();
                }
                HttpContext.Session.SetString(SessionKeyConversation, JsonSerializer.Serialize(conv));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No se pudo guardar conversation en session (no crítico).");
            }
        }

        private ReservationDraft? GetDraftFromSession()
        {
            var s = HttpContext.Session.GetString(SessionKeyDraft);
            if (string.IsNullOrEmpty(s)) return null;
            try { return JsonSerializer.Deserialize<ReservationDraft>(s); }
            catch
            {
                _logger.LogWarning("Draft en sesión con formato inválido; se reinicia.");
                HttpContext.Session.Remove(SessionKeyDraft);
                return null;
            }
        }

        private void SaveDraftToSession(ReservationDraft draft)
        {
            try
            {
                HttpContext.Session.SetString(SessionKeyDraft, JsonSerializer.Serialize(draft));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No se pudo guardar draft en session (no crítico).");
            }
        }

        #endregion

        #region Heurísticos y utilidades

        private async Task<int?> TryExtractExperienciaIdFromTextAsync(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var tNorm = NormalizeForMatching(text);

            foreach (var code in new[] { "01", "02", "03", "1", "2", "3" })
            {
                if (tNorm.Contains($" {code} ") || tNorm.Contains($"({code})") || tNorm.Contains($"\"{code}\"") || tNorm.Contains($":{code}"))
                {
                    var byCodigo = await _context.Experiencias
                        .AsNoTracking()
                        .FirstOrDefaultAsync(e => e.Codigo == code || e.Codigo == code.PadLeft(2, '0'));
                    if (byCodigo != null) return byCodigo.Id;
                }
            }

            var experiencias = await _context.Experiencias.AsNoTracking().ToListAsync();
            foreach (var e in experiencias)
            {
                var name = (e.Nombre ?? "").Trim();
                if (string.IsNullOrWhiteSpace(name)) continue;
                var nameNorm = NormalizeForMatching(name);
                // evitar matches con tokens muy cortos
                if (string.IsNullOrWhiteSpace(tNorm) || tNorm.Length < 2) continue;

                // coincidencia por palabra completa
                if (Regex.IsMatch(tNorm, $"\\b{Regex.Escape(nameNorm)}\\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase))
                    return e.Id;

                // coincidencia por la primera palabra del nombre (si es significativa)
                var first = nameNorm.Split(' ')[0];
                if (first.Length >= 3 && Regex.IsMatch(tNorm, $"\\b{Regex.Escape(first)}\\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase))
                    return e.Id;

                // coincidencias por root específico
                if (nameNorm.Contains("degust") && tNorm.Contains("degust")) return e.Id;
                if (nameNorm.Contains("inmers") && tNorm.Contains("inmers")) return e.Id;
                if (nameNorm.Contains("theobrom") && tNorm.Contains("theobrom")) return e.Id;
            }

            return null;
        }

        private string? TryExtractRestriccionesFromText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var found = new List<string>();
            var lower = NormalizeForMatching(text);

            // 1) Frases explícitas que SÍ cuentan como restricción (sin acentos)
            var explicitPatterns = new[]
            {
                @"\bsin\s+gluten\b",
                @"\b(contiene|con|tiene)\s+gluten\b",
                @"\balerg(?:ia|ico).*gluten\b",
                @"\bno\s+come\s+gluten\b",
                @"\bsin\s+lacteo\b",
                @"\b(no\s+como|no\s+consumo)\b",
                @"\b(vegetariano|vegetariana|vegano|vegana)\b",
                @"\balerg(?:ia|ico|icos)\b"
            };

            foreach (var pat in explicitPatterns)
            {
                var m = Regex.Matches(lower, pat, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                foreach (Match mm in m)
                    if (!string.IsNullOrWhiteSpace(mm.Value))
                        found.Add(mm.Value.Trim());
            }

            // 2) "no me gusta X" / "odio X" -> extraer X, pero **filtrar gluten** a menos que venga en contexto explícito
            var dislikeMatches = Regex.Matches(text, @"\b(?:no me gusta|no me gusta mucho|odio|no me agrada)\s+([A-Za-zñÑáéíóúÁÉÍÓÚ0-9\s\-]{1,60})",
                                                 RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            foreach (Match m in dislikeMatches)
            {
                if (m.Groups.Count > 1)
                {
                    var candidate = m.Groups[1].Value.Trim();
                    var candNorm = NormalizeForMatching(candidate);
                    if (Regex.IsMatch(candNorm, @"\bgluten\b"))
                        continue; // no inferir gluten desde un "no me gusta" sin más contexto
                    found.Add(candidate);
                }
            }

            // 3) "alergia a X" -> permitir (si pone "alergia al gluten" sí lo registra)
            var alergMatches = Regex.Matches(text, @"\balerg(?:ia|ico|ica|icos|icas)\s+(?:a\s+)?([A-Za-zñÑáéíóúÁÉÍÓÚ0-9\s\-]{1,80})",
                                             RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            foreach (Match m in alergMatches)
            {
                if (m.Groups.Count > 1)
                {
                    var candidate = m.Groups[1].Value.Trim();
                    if (!string.IsNullOrWhiteSpace(candidate)) found.Add(candidate);
                }
            }

            if (found.Count == 0) return null;

            var normalized = found
                .Select(x => NormalizeRestrictionToken(x))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct()
                .ToList();

            return normalized.Count > 0 ? string.Join("; ", normalized) : null;
        }

        // Extrae preferencias (gustos/likes/dislikes) desde un texto.
        // Devuelve null si no detecta nada claramente relacionado a "preferencias".
        private string? TryExtractPreferencesFromText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var lower = NormalizeForMatching(text);

            var found = new List<string>();

            // patrones "me gusta X", "prefiero X", "mi preferencia es X", "me gustaría X", "me encanta X"
            var prefMatches = Regex.Matches(text, @"(?:me gusta|me encant(?:a|o)|prefiero|mi preferencia es|mi preferencia:|me gustaria|me gustarí(?:a|an)|prefiero)\s+([A-Za-zñÑáéíóúÁÉÍÓÚ0-9\s\-]{1,80})",
                                             RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            foreach (Match m in prefMatches)
            {
                if (m.Groups.Count > 1) found.Add(m.Groups[1].Value.Trim());
            }

            // frases explícitas "preferencias: ..." o "mis preferencias son ..."
            var labelMatch = Regex.Match(text, @"(?:preferencias?:|mis preferencias son)\s*([A-Za-z0-9ñÑáéíóúÁÉÍÓÚ\.,;:\s\-]{1,200})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (labelMatch.Success && labelMatch.Groups.Count > 1)
            {
                found.Add(labelMatch.Groups[1].Value.Trim());
            }

            // si no hay matches claros, comprobar si el texto contiene palabras que denotan que hablamos de preferencias
            if (found.Count == 0)
            {
                if (lower.Contains("prefe") || lower.Contains("preferencias") || lower.Contains("me gustaria") || lower.Contains("me gust")
                    || lower.Contains("me encanta") || lower.Contains("odio") || lower.Contains("preferir"))
                {
                    // fallback: guardar el texto completo (limitado)
                    var txt = text.Trim();
                    if (txt.Length > 400) txt = txt.Substring(0, 400);
                    return txt;
                }
                return null;
            }

            // normalizar tokens y unir
            var normalized = found
                .Select(x => Regex.Replace(x, @"\s+", " ").Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct()
                .ToList();

            return normalized.Count > 0 ? string.Join("; ", normalized) : null;
        }


        // Merge / normaliza restricciones evitando duplicados
        private string? MergeRestrictions(string? existing, string? incoming)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(existing))
            {
                parts.AddRange(SplitRestrictionParts(existing));
            }
            if (!string.IsNullOrWhiteSpace(incoming))
            {
                parts.AddRange(SplitRestrictionParts(incoming));
            }

            var uniq = parts
                .Select(x => NormalizeRestrictionToken(x))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct()
                .ToList();

            if (uniq.Count == 0) return null;

            return string.Join("; ", uniq);
        }

        private IEnumerable<string> SplitRestrictionParts(string s)
        {
            return s.Split(new[] { ';', ',', '/', '|', '.' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => p.Trim())
                    .Where(p => !string.IsNullOrWhiteSpace(p));
        }

        private string NormalizeRestrictionToken(string tok)
        {
            if (string.IsNullOrWhiteSpace(tok)) return "";

            var x = NormalizeForMatching(tok);
            x = Regex.Replace(x, @"\s+", " ");

            if (x.Contains("sin gluten")) return "sin gluten";
            if (Regex.IsMatch(x, @"\b(contiene|con|tiene)\s+gluten\b")) return "contiene gluten";
            if (x.Contains("alerg") && x.Contains("gluten")) return "alergia al gluten";
            if (x.Contains("sin lacteo") || x.Contains("sin lacteos")) return "sin lactosa";
            if (x.Contains("vegetar")) return "vegetariano";
            if (x.Contains("vegano")) return "vegano";

            if (x.StartsWith("no me gusta")) return x.Replace("no me gusta mucho ", "no me gusta ");

            x = x.Trim().Trim(',', ';', '.', ':', '-');
            return x;
        }

        // ChatController.cs (reemplazar NextStepFromDraft)
        private string NextStepFromDraft(ReservationDraft d)
        {
            if (d == null) return "ask_experiencia";

            // 1 - Experiencia
            if (!d.ExperienciaId.HasValue) return "ask_experiencia";

            // 2 - Personas
            if (d.Personas <= 0) return "ask_people";

            // 3 - Restricciones (si no las especificó, preguntamos; si dice "no", el bot debe guardar "Ninguna" o null)
            if (string.IsNullOrWhiteSpace(d.Restricciones)) return "ask_restricciones";

            // 4 - Día
            if (string.IsNullOrWhiteSpace(d.Dia)) return "ask_day";

            // 5 - Hora
            if (string.IsNullOrWhiteSpace(d.Hora)) return "ask_time";

            // 6 - Nombre de la persona que firma la reserva
            if (string.IsNullOrWhiteSpace(d.NombreUsuario)) return "ask_name";

            // 7 - DNI
            if (string.IsNullOrWhiteSpace(d.Dni)) return "ask_dni";

            // 8 - Teléfono
            if (string.IsNullOrWhiteSpace(d.Telefono)) return "ask_phone";

            // 9 - Preferencias (preguntar explícitamente si quiere guardar preferencias)
            // PreferenciasJson actúa como indicador de que ya respondió
            if (string.IsNullOrWhiteSpace(d.PreferenciasJson)) return "ask_prefs";

            // 10 - todo ok
            return "done";
        }



        private async Task<string> GetNombreExperiencia(int experienciaId, CancellationToken cancellationToken = default)
        {
            var e = await _context.Experiencias.AsNoTracking().FirstOrDefaultAsync(x => x.Id == experienciaId, cancellationToken);
            return e?.Nombre ?? experienciaId.ToString();
        }

        #endregion

        #region Util: parse fecha/hora

        private DateTime ParseFechaHora(string? dia, string? hora)
        {
            if (string.IsNullOrWhiteSpace(dia) && string.IsNullOrWhiteSpace(hora)) return DateTime.MinValue;

            dia = (dia ?? "").Trim();
            hora = (hora ?? "").Trim();

            var combined = string.IsNullOrWhiteSpace(hora) ? dia : $"{dia} {hora}";

            DateTime dt;
            var formats = new[] {
                "yyyy-MM-dd HH:mm", "yyyy-MM-dd H:mm", "yyyy-MM-dd",
                "dd/MM/yyyy HH:mm", "d/M/yyyy H:mm", "d/M/yyyy",
                "dd-MM-yyyy HH:mm", "dd-MM-yyyy", "yyyy/MM/dd HH:mm", "yyyy/MM/dd"
            };

            if (DateTime.TryParseExact(combined, formats, new CultureInfo("es-PE"), DateTimeStyles.AssumeLocal, out dt))
                return dt;

            if (DateTime.TryParse(combined, new CultureInfo("es-PE"), DateTimeStyles.AssumeLocal, out dt))
                return dt;

            var low = NormalizeForMatching(dia + " " + hora);
            if (low.Contains("hoy"))
            {
                var baseDate = DateTime.UtcNow.ToLocalTime().Date;
                if (DateTime.TryParse(hora, out var h2)) return baseDate.AddHours(h2.Hour).AddMinutes(h2.Minute);
                return baseDate;
            }
            if (low.Contains("manana") || low.Contains("mañana"))
            {
                var baseDate = DateTime.UtcNow.ToLocalTime().Date.AddDays(1);
                if (DateTime.TryParse(hora, out var h3)) return baseDate.AddHours(h3.Hour).AddMinutes(h3.Minute);
                return baseDate;
            }

            return DateTime.MinValue;
        }

        #endregion

        #region Contact extraction

        // Extrae nombre / dni / telefono simple desde un texto (IA o input usuario)
        private (string? Name, string? Dni, string? Telefono)? TryExtractContactInfoFromText(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var t = text;

            string? name = null;
            string? dni = null;
            string? telefono = null;

            // 1) Buscar DNI etiquetado "DNI: 12345678" o "DNI 12345678" - prioridad
            var dniLabelMatch = Regex.Match(t, @"\b(?:DNI[:\s]*|documento[:\s]*|doc[:\s]*)?(\d{8})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (dniLabelMatch.Success)
                dni = dniLabelMatch.Groups[1].Value;

            // 2) Teléfono: soportar +51 con separadores o 9XXXXXXXX
            var telMatch = Regex.Match(t, @"(\+?51[\s\-]?[9]{0,1}[\s\-]?\d{3}[\s\-]?\d{3}[\s\-]?\d{3}|\b9\d{8}\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (telMatch.Success)
            {
                telefono = Regex.Replace(telMatch.Groups[1].Value, @"[\s\-]", "");
            }

            // Nombre heurístico: buscar "me llamo X", "mi nombre es X", "soy X" (hasta 4 palabras capitalizadas)
            var nameMatch = Regex.Match(t, @"(?:me llamo|mi nombre es|soy)\s+([A-ZÑÁÉÍÓÚ][\wñáéíóúÁÉÍÓÚ-]+(?:\s+[A-ZÑÁÉÍÓÚ][\wñáéíóúÁÉÍÓÚ-]+){0,3})", RegexOptions.IgnoreCase);
            if (nameMatch.Success) name = nameMatch.Groups[1].Value.Trim();

            // fallback: "Nombre: Joel Uriol Sandoval" o "Nombre a nombre: Joel ..."
            if (string.IsNullOrWhiteSpace(name))
            {
                var labelMatch = Regex.Match(t, @"(?:nombre(?: a nombre)?:|nombre completo:)\s*([A-Za-zÁÉÍÓÚáéíóúñÑ0-9\s\-]+)", RegexOptions.IgnoreCase);
                if (labelMatch.Success) name = labelMatch.Groups[1].Value.Trim();
            }

            // Sanitize simple
            if (!string.IsNullOrWhiteSpace(name)) name = Regex.Replace(name, @"\s{2,}", " ").Trim();

            if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(dni) && string.IsNullOrWhiteSpace(telefono)) return null;
            return (Name: name, Dni: dni, Telefono: telefono);
        }

        #endregion

        #region Helpers text normalization

        private static string NormalizeForMatching(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;
            return RemoveDiacritics(s).ToLowerInvariant();
        }

        private static string RemoveDiacritics(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            var normalized = text.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder();
            foreach (var ch in normalized)
            {
                var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
                if (cat != UnicodeCategory.NonSpacingMark) sb.Append(ch);
            }
            return sb.ToString().Normalize(NormalizationForm.FormC);
        }

        #endregion
    }
}
