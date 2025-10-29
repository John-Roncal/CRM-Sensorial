using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AppWebCentralRestaurante.Models
{
    [Table("Reservas")]
    public class Reserva
    {
        [Key]
        public int Id { get; set; }

        public int? UsuarioId { get; set; }

        [Required, MaxLength(150)]
        public string NombreReserva { get; set; }

        [Required]
        public int NumComensales { get; set; } = 1;

        [Required]
        public int ExperienciaId { get; set; } // FK a Experiencias

        public string Restricciones { get; set; } // alergias/observaciones

        [Required]
        public DateTime FechaHora { get; set; }

        [Required, MaxLength(30)]
        public string Estado { get; set; } = "pendiente"; // 'pendiente','confirmada','cancelada','completada'

        public DateTime CreadoEn { get; set; } = DateTime.UtcNow;

        public DateTime? ActualizadoEn { get; set; }

        // Navegación
        [ForeignKey(nameof(UsuarioId))]
        public Usuario Usuario { get; set; }

        [ForeignKey(nameof(ExperienciaId))]
        public Experiencia Experiencia { get; set; }

        public ICollection<RecomendacionLog> RecomendacionesLog { get; set; }
    }
}
