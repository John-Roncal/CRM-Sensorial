using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AppWebCentralRestaurante.Models
{
    [Table("EmailVerificaciones")]
    public class EmailVerificacion
    {
        [Key]
        public int Id { get; set; }

        [Required]
        public int UsuarioId { get; set; }

        [Required, MaxLength(200)]
        public string Token { get; set; }

        // Fecha/hora en que expira el token
        public DateTime ExpiraEn { get; set; }

        // Si ya fue usado (confirmado)
        public bool Usado { get; set; } = false;

        public DateTime CreadoEn { get; set; } = DateTime.UtcNow;

        // Navegación al usuario (opcional)
        [ForeignKey(nameof(UsuarioId))]
        public Usuario Usuario { get; set; }
    }
}
