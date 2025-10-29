using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AppWebCentralRestaurante.Models
{
    [Table("RecomendacionesLog")]
    public class RecomendacionLog
    {
        [Key]
        public int Id { get; set; }

        public int? UsuarioId { get; set; }

        public int? ReservaId { get; set; }

        public int? ExperienciaId { get; set; }

        public double? Score { get; set; } // puntuación devuelta por el modelo

        public string CaracteristicasJson { get; set; } // contexto de la predicción

        public DateTime CreadoEn { get; set; } = DateTime.UtcNow;

        // Navegación
        [ForeignKey(nameof(UsuarioId))]
        public Usuario Usuario { get; set; }

        [ForeignKey(nameof(ReservaId))]
        public Reserva Reserva { get; set; }

        [ForeignKey(nameof(ExperienciaId))]
        public Experiencia Experiencia { get; set; }
    }
}
