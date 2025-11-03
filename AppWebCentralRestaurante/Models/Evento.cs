using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AppWebCentralRestaurante.Models
{
    public class Evento
    {
        [Key]
        public Guid EventoId { get; set; } = Guid.NewGuid();

        [Required, MaxLength(150)]
        public string EventType { get; set; }

        public int? UsuarioId { get; set; }

        public Guid? AnonId { get; set; }

        public Guid? ConversationId { get; set; }

        [MaxLength(200)]
        public string SenderId { get; set; }

        [Column(TypeName = "nvarchar(max)")]
        public string Payload { get; set; }

        public DateTimeOffset CreadoEn { get; set; } = DateTimeOffset.UtcNow;

        // Navigation (opcional pero útil)
        public Usuario Usuario { get; set; }
        // public AnonSession AnonSession { get; set; } // si quieres navegación
    }
}
