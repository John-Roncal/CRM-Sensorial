using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AppWebCentralRestaurante.Models
{
    [Table("Usuarios")]
    public class Usuario
    {
        [Key]
        public int Id { get; set; }

        [Required, MaxLength(150)]
        public string Nombre { get; set; }

        [Required, MaxLength(256), EmailAddress]
        public string Email { get; set; }

        // Contraseña hasheada (nulo si el usuario solo se registró vía Firebase sin crear password local)
        public string PasswordHash { get; set; }

        [Required, MaxLength(20)]
        public string Rol { get; set; } = "Cliente"; // 'Cliente','Mozo','Chef','Admin'

        // Indica que el email fue verificado (se puede marcar tras confirmación con Firebase)
        public bool EmailConfirmado { get; set; } = false;

        // UID del usuario en Firebase (opcional pero útil para enlazar cuentas)
        [MaxLength(200)]
        public string FirebaseUid { get; set; }

        public DateTime CreadoEn { get; set; } = DateTime.UtcNow;
        public DateTime? ActualizadoEn { get; set; }

        // Navegación (opcional, mantenlas si ya usas estas entidades)
        public virtual ICollection<Reserva> Reservas { get; set; }
        public virtual ICollection<Preferencia> Preferencias { get; set; }
        public virtual ICollection<Reporte> ReportesGenerados { get; set; }
    }
}
