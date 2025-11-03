using System;
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

        [Column(TypeName = "nvarchar(max)")]
        public string DatosJson { get; set; }

        public DateTimeOffset CreadoEn { get; set; } = DateTimeOffset.UtcNow;

        // Navigation
        public Usuario Usuario { get; set; }
    }
}
