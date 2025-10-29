// Services/ICohereService.cs
using System.Threading;
using System.Threading.Tasks;

namespace AppWebCentralRestaurante.Services
{
    /// <summary>
    /// Resultado devuelto por el servicio de IA (Cohere u otro).
    /// Contiene el texto de respuesta del bot y campos estructurados extraídos.
    /// </summary>
    public class CohereResult
    {
        /// <summary>Respuesta en lenguaje natural del asistente.</summary>
        public string BotReply { get; set; } = string.Empty;

        /// <summary>Campo extraído: día (texto libre, p. ej. "2025-10-15" o "sábado 18").</summary>
        public string? Dia { get; set; }

        /// <summary>Campo extraído: hora (texto, p. ej. "20:00").</summary>
        public string? Hora { get; set; }

        /// <summary>Campo extraído: número de personas (si se pudo extraer).</summary>
        public int? Personas { get; set; }

        /// <summary>Código o nombre corto de la experiencia detectada (p. ej. "01", "degustación").</summary>
        public string? ExperienciaCode { get; set; }

        /// <summary>Si el servicio devuelve o se mapea a un Id de experiencia local, se puede usar aquí.</summary>
        public int? ExperienciaId { get; set; }

        /// <summary>Restricciones alimentarias detectadas (texto libre, p. ej. "sin gluten; vegetariano").</summary>
        public string? Restricciones { get; set; }

        /// <summary>Si la IA extrajo el nombre completo de la persona que hace la reserva.</summary>
        public string? ClienteNombre { get; set; }

        /// <summary>DNI / documento de identidad (cuando la IA lo detecta, p. ej. 8 dígitos).</summary>
        public string? ClienteDni { get; set; }

        /// <summary>Teléfono / celular detectado (texto libre).</summary>
        public string? ClienteTelefono { get; set; }

        /// <summary>Respuesta completa cruda de la API (útil para logging y ML).</summary>
        public string RawResponse { get; set; } = string.Empty;

        /// <summary>Valor opcional de confianza / score devuelto por la IA (si aplica).</summary>
        public double? Confidence { get; set; }
    }

    /// <summary>
    /// Contrato del servicio que habla con la API de IA (Cohere u otro).
    /// Devuelve un CohereResult con texto y campos estructurados.
    /// </summary>
    public interface ICohereService
    {
        /// <summary>
        /// Envía el historial de conversación y el texto del usuario al motor de IA,
        /// y devuelve la respuesta del asistente junto a los campos extraídos.
        /// </summary>
        /// <param name="conversationSoFar">Historial de la conversación (texto) que ayudará al modelo.</param>
        /// <param name="userText">Texto de la última intervención del usuario.</param>
        /// <param name="ct">Token de cancelación opcional.</param>
        Task<CohereResult> SendConversationAndExtractAsync(string conversationSoFar, string userText, CancellationToken ct = default);
    }
}
