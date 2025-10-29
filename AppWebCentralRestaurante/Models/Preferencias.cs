using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AppWebCentralRestaurante.Models
{
    [Table("Preferencias")]
    public class Preferencia
    {
        [Key]
        public int Id { get; set; }

        [Required]
        public int UsuarioId { get; set; }

        public string DatosJson { get; set; } // estructura flexible (likes, dislikes, alergias...)

        public DateTime CreadoEn { get; set; } = DateTime.UtcNow;

        public DateTime? ActualizadoEn { get; set; }

        // Navegación
        [ForeignKey(nameof(UsuarioId))]
        public Usuario Usuario { get; set; }
    }
}
