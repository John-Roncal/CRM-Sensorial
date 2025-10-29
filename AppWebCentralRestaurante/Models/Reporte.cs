using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AppWebCentralRestaurante.Models
{
    [Table("Reportes")]
    public class Reporte
    {
        [Key]
        public int Id { get; set; }

        [Required]
        public int AdminId { get; set; }

        [Required, MaxLength(150)]
        public string TipoReporte { get; set; } // ej. 'reservas_por_dia'

        public string ParametrosJson { get; set; }

        public string RutaArchivo { get; set; } // ruta donde se exportó el archivo si aplica

        public DateTime CreadoEn { get; set; } = DateTime.UtcNow;

        // Navegación
        [ForeignKey(nameof(AdminId))]
        public Usuario Admin { get; set; }
    }
}
