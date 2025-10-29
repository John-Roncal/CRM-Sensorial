// Services/CohereChatService.cs
using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AppWebCentralRestaurante.Services
{
    /// <summary>
    /// Servicio que llama a la API de Cohere (o a un endpoint REST-compatible).
    /// Registrar como typed client:
    /// builder.Services.AddHttpClient<ICohereService, CohereChatService>();
    /// </summary>
    public class CohereChatService : ICohereService
    {
        private readonly HttpClient _httpClient;
        private readonly IConfiguration _cfg;
        private readonly ILogger<CohereChatService> _logger;
        private readonly string _apiKey;
        private readonly string _endpoint;
        private readonly string _model;
        private readonly JsonSerializerOptions _jsonOpts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        public CohereChatService(HttpClient httpClient, IConfiguration cfg, ILogger<CohereChatService> logger)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            _apiKey = cfg["Cohere:ApiKey"] ?? Environment.GetEnvironmentVariable("COHERE_API_KEY") ?? string.Empty;
            _endpoint = cfg["Cohere:Endpoint"] ?? "https://api.cohere.ai/chat";
            _model = cfg["Cohere:Model"] ?? "command-xlarge-nightly";

            // Si API key está presente y el HttpClient no tiene Authorization, añadirla por defecto
            if (!string.IsNullOrWhiteSpace(_apiKey) && _httpClient.DefaultRequestHeaders.Authorization == null)
            {
                _httpClient.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);
            }

            if (string.IsNullOrWhiteSpace(_apiKey))
            {
                _logger.LogWarning("Cohere API key no configurada (Cohere:ApiKey o COHERE_API_KEY). Las peticiones pueden fallar.");
            }
        }

        public async Task<CohereResult> SendConversationAndExtractAsync(string conversationSoFar, string userText, CancellationToken ct = default)
        {
            // System prompt instructing the assistant to append a JSON block with the fields we need.
            var system = @"Eres un asistente conversacional especializado en reservas para Central Restaurante.
Responde de forma natural y cordial. Al final de tu respuesta, añade exactamente en una línea el marcador:
---JSON---
y en las siguientes líneas un JSON válido con las claves (si están disponibles): dia, hora, personas, experiencia, restricciones, nombre, dni, telefono.
Ejemplo:
---JSON---
{""dia"":""2025-10-20"", ""hora"":""20:00"", ""personas"":3, ""experiencia"":""01"", ""restricciones"":""sin gluten"", ""nombre"":""Juan Perez"", ""dni"":""71234567"", ""telefono"":""987654321""}

Si no puedes extraer un campo, pon null (para personas usa null). No añadas texto después del JSON. No uses otro delimitador.";

            var conversation = string.IsNullOrWhiteSpace(conversationSoFar) ? "" : conversationSoFar + "\n";
            var prompt = system + "\n\n" +
                         "Historial de la conversación (usuario y asistente):\n" +
                         conversation +
                         $"Usuario: {userText}\nAsistente:";

            // Construcción de body; algunos endpoints esperan "message"/"messages" o "input", mantenemos compatibilidad con shapes simples.
            var bodyObj = new
            {
                model = _model,
                message = prompt,
                max_tokens = 400,
                temperature = 0.4
            };

            var jsonBody = JsonSerializer.Serialize(bodyObj);

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, _endpoint);

                // En caso la apiKey no esté en DefaultRequestHeaders (por seguridad), añadirla aquí.
                if (!string.IsNullOrWhiteSpace(_apiKey) && req.Headers.Authorization == null && _httpClient.DefaultRequestHeaders.Authorization == null)
                {
                    req.Headers.Add("Authorization", $"Bearer {_apiKey}");
                }

                req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

                using var resp = await _httpClient.SendAsync(req, ct);
                var respText = await resp.Content.ReadAsStringAsync(ct);

                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Cohere API returned non-success {Status}: {Snippet}",
                        resp.StatusCode,
                        (respText ?? "").Length > 300 ? respText.Substring(0, 300) : respText);
                    // Intentamos parsear igualmente para una UX más amable
                }

                // Guardado opcional del raw response (mejor para debugging); no bloquear en fallo
                try
                {
                    var dir = Path.Combine(AppContext.BaseDirectory, "CohereResponses");
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    var fname = Path.Combine(dir, $"cohere_{DateTime.UtcNow:yyyyMMddTHHmmssZ}.json");
                    await File.WriteAllTextAsync(fname, respText, ct);
                }
                catch (Exception writeEx)
                {
                    _logger.LogDebug(writeEx, "No se pudo guardar response raw (no crítico).");
                }

                // Extraer texto principal (soporta varias formas de respuesta JSON)
                string botFullText = ExtractTextFromResponse(respText) ?? respText ?? string.Empty;

                // Separar texto natural y JSON (buscamos el marcador ---JSON--- o el último objeto JSON)
                string botReply = botFullText;
                string? jsonPart = null;

                // Normalizar saltos y buscar marcador
                var jsonMarkerRegex = new Regex(@"\r?\n?---JSON---\r?\n?", RegexOptions.IgnoreCase);
                var markerMatch = jsonMarkerRegex.Match(botFullText);
                if (markerMatch.Success)
                {
                    botReply = botFullText.Substring(0, markerMatch.Index).Trim();
                    jsonPart = botFullText.Substring(markerMatch.Index + markerMatch.Length).Trim();
                }
                else
                {
                    // Fallback: buscar el último bloque JSON {...}
                    var lastOpen = botFullText.LastIndexOf('{');
                    var lastClose = botFullText.LastIndexOf('}');
                    if (lastOpen >= 0 && lastClose > lastOpen)
                    {
                        jsonPart = botFullText.Substring(lastOpen, lastClose - lastOpen + 1);
                        botReply = botFullText.Substring(0, lastOpen).Trim();
                    }
                }

                // Variables a devolver
                string? dia = null;
                string? hora = null;
                int? personas = null;
                string? experienciaCode = null;
                int? experienciaId = null;
                string? restricciones = null;
                double? confidence = null;

                // nuevos: contacto
                string? clienteNombre = null;
                string? clienteDni = null;
                string? clienteTelefono = null;

                if (!string.IsNullOrWhiteSpace(jsonPart))
                {
                    try
                    {
                        // A veces el JSON contiene comas finales o texto no-estricto -> intentamos limpiar
                        var cleaned = jsonPart.Trim();
                        // Si hay texto antes/after que no pertenece al JSON, JsonDocument.Parse lanzará; capturamos.
                        using var doc = JsonDocument.Parse(cleaned);
                        var root = doc.RootElement;

                        if (root.TryGetProperty("dia", out var pDia) && pDia.ValueKind == JsonValueKind.String)
                            dia = pDia.GetString();

                        if (root.TryGetProperty("hora", out var pHora) && pHora.ValueKind == JsonValueKind.String)
                            hora = pHora.GetString();

                        if (root.TryGetProperty("personas", out var pPers))
                        {
                            if (pPers.ValueKind == JsonValueKind.Number && pPers.TryGetInt32(out var n)) personas = n;
                            else if (pPers.ValueKind == JsonValueKind.String && int.TryParse(pPers.GetString(), out var m)) personas = m;
                        }

                        if (root.TryGetProperty("experiencia", out var pExp))
                        {
                            if (pExp.ValueKind == JsonValueKind.String)
                                experienciaCode = pExp.GetString();
                            else if (pExp.ValueKind == JsonValueKind.Number && pExp.TryGetInt32(out var eid))
                                experienciaId = eid;
                        }

                        // also accept "experiencia_code"
                        if (root.TryGetProperty("experiencia_code", out var pExpCode) && pExpCode.ValueKind == JsonValueKind.String)
                            experienciaCode ??= pExpCode.GetString();

                        if (root.TryGetProperty("restricciones", out var pRes) && pRes.ValueKind == JsonValueKind.String)
                            restricciones = pRes.GetString();

                        if (root.TryGetProperty("confidence", out var pConf) && pConf.ValueKind == JsonValueKind.Number && pConf.TryGetDouble(out var conf))
                            confidence = conf;

                        // nuevos campos de cliente
                        if (root.TryGetProperty("nombre", out var pNombre) && pNombre.ValueKind == JsonValueKind.String)
                            clienteNombre = pNombre.GetString();

                        if (root.TryGetProperty("dni", out var pDni) && pDni.ValueKind == JsonValueKind.String)
                            clienteDni = pDni.GetString();

                        if (root.TryGetProperty("telefono", out var pTel) && pTel.ValueKind == JsonValueKind.String)
                            clienteTelefono = pTel.GetString();
                    }
                    catch (Exception parseEx)
                    {
                        // Si falla parsear el bloque JSON tal cual, intentamos heurística extraer JSON con regex (último {...})
                        _logger.LogDebug(parseEx, "No se pudo parsear el bloque JSON extraído del asistente directamente, intentando heurística.");
                        var jsonMatch = Regex.Match(jsonPart, @"\{[\s\S]*\}$");
                        if (jsonMatch.Success)
                        {
                            try
                            {
                                using var doc = JsonDocument.Parse(jsonMatch.Value);
                                var root = doc.RootElement;
                                if (root.TryGetProperty("dia", out var pDia) && pDia.ValueKind == JsonValueKind.String)
                                    dia = pDia.GetString();
                                if (root.TryGetProperty("hora", out var pHora) && pHora.ValueKind == JsonValueKind.String)
                                    hora = pHora.GetString();
                                if (root.TryGetProperty("personas", out var pPers))
                                {
                                    if (pPers.ValueKind == JsonValueKind.Number && pPers.TryGetInt32(out var n)) personas = n;
                                    else if (pPers.ValueKind == JsonValueKind.String && int.TryParse(pPers.GetString(), out var m)) personas = m;
                                }
                                if (root.TryGetProperty("experiencia", out var pExp))
                                {
                                    if (pExp.ValueKind == JsonValueKind.String)
                                        experienciaCode = pExp.GetString();
                                    else if (pExp.ValueKind == JsonValueKind.Number && pExp.TryGetInt32(out var eid))
                                        experienciaId = eid;
                                }
                                if (root.TryGetProperty("restricciones", out var pRes) && pRes.ValueKind == JsonValueKind.String)
                                    restricciones = pRes.GetString();
                                if (root.TryGetProperty("nombre", out var pNombre) && pNombre.ValueKind == JsonValueKind.String)
                                    clienteNombre = pNombre.GetString();
                                if (root.TryGetProperty("dni", out var pDni) && pDni.ValueKind == JsonValueKind.String)
                                    clienteDni = pDni.GetString();
                                if (root.TryGetProperty("telefono", out var pTel) && pTel.ValueKind == JsonValueKind.String)
                                    clienteTelefono = pTel.GetString();
                            }
                            catch (Exception innerEx)
                            {
                                _logger.LogDebug(innerEx, "Heurística de JSON falló (no crítico).");
                            }
                        }
                    }
                }

                // fallback: si experiecnia vino como código textual convertible a int
                if (!experienciaId.HasValue && !string.IsNullOrWhiteSpace(experienciaCode))
                {
                    if (int.TryParse(experienciaCode, out var tmp)) experienciaId = tmp;
                }

                // heurística simple en texto completo si aún falta información
                if (string.IsNullOrWhiteSpace(experienciaCode) && string.IsNullOrWhiteSpace(restricciones))
                {
                    var low = botFullText.ToLowerInvariant();
                    if (low.Contains("degust") || low.Contains("menú degustación") || low.Contains("menu degustacion")) experienciaCode = "01";
                    else if (low.Contains("inmers") || low.Contains("inmersión") || low.Contains("inmersion")) experienciaCode = "02";
                    else if (low.Contains("theobrom") || low.Contains("cacao")) experienciaCode = "03";

                    var keyWords = new[] { "sin gluten", "gluten", "vegetar", "vegano", "sin lactosa", "lactosa", "alerg" };
                    var found = new System.Collections.Generic.List<string>();
                    foreach (var k in keyWords)
                        if (low.Contains(k)) found.Add(k);
                    if (found.Count > 0) restricciones = string.Join("; ", found);
                }

                return new CohereResult
                {
                    BotReply = string.IsNullOrWhiteSpace(botReply) ? "[Sin respuesta del asistente]" : botReply,
                    Dia = dia,
                    Hora = hora,
                    Personas = personas,
                    ExperienciaCode = experienciaCode,
                    ExperienciaId = experienciaId,
                    Restricciones = restricciones,
                    ClienteNombre = clienteNombre,
                    ClienteDni = clienteDni,
                    ClienteTelefono = clienteTelefono,
                    RawResponse = respText,
                    Confidence = confidence
                };
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Petición a Cohere cancelada por token.");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error llamando a Cohere API.");
                return new CohereResult
                {
                    BotReply = "Error contactando el servicio de IA. Intenta nuevamente más tarde.",
                    RawResponse = ex.ToString()
                };
            }
        }

        /// <summary>
        /// Extrae el texto principal desde distintos shapes de respuesta JSON.
        /// Soporta: output[0].content[0].text, choices[0].message.content, text, response, etc.
        /// </summary>
        private static string? ExtractTextFromResponse(string respText)
        {
            if (string.IsNullOrWhiteSpace(respText)) return null;
            try
            {
                using var doc = JsonDocument.Parse(respText);
                var root = doc.RootElement;

                // varios caminos posibles que distintas APIs usan
                if (TryGetElementString(root, new[] { "output", "0", "content", "0", "text" }, out var v) && !string.IsNullOrWhiteSpace(v)) return v;
                if (TryGetElementString(root, new[] { "choices", "0", "message", "content" }, out v) && !string.IsNullOrWhiteSpace(v)) return v;
                if (TryGetElementString(root, new[] { "choices", "0", "text" }, out v) && !string.IsNullOrWhiteSpace(v)) return v;
                if (TryGetElementString(root, new[] { "text" }, out v) && !string.IsNullOrWhiteSpace(v)) return v;
                if (TryGetElementString(root, new[] { "response" }, out v) && !string.IsNullOrWhiteSpace(v)) return v;

                // si ninguna ruta coincide, intentar buscar la primera string larga dentro del documento (heurística)
                foreach (var el in root.EnumerateObject())
                {
                    if (el.Value.ValueKind == JsonValueKind.String && el.Value.GetString() is string s && s.Length > 0)
                        return s;
                }
            }
            catch
            {
                // no es JSON o no contiene las rutas esperadas -> devolver null
            }
            return null;
        }

        private static bool TryGetElementString(JsonElement el, string[] path, out string value)
        {
            value = null!;
            JsonElement cur = el;
            foreach (var p in path)
            {
                if (int.TryParse(p, out int idx))
                {
                    if (cur.ValueKind == JsonValueKind.Array && cur.GetArrayLength() > idx) cur = cur[idx];
                    else return false;
                }
                else
                {
                    if (cur.ValueKind == JsonValueKind.Object && cur.TryGetProperty(p, out var next)) cur = next;
                    else return false;
                }
            }
            if (cur.ValueKind == JsonValueKind.String) { value = cur.GetString() ?? ""; return true; }
            value = cur.ToString(); return true;
        }
    }
}
