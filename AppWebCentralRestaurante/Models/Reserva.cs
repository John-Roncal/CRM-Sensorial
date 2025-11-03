using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AppWebCentralRestaurante.Models
{
    [Table("Reservas")]
    public class Reserva
    {
        public int Id { get; set; }
        public int? UsuarioId { get; set; }
        public Usuario Usuario { get; set; }
        public Guid? AnonId { get; set; }
        public int ExperienciaId { get; set; }
        public Experiencia Experiencia { get; set; }
        public int NumComensales { get; set; }
        public DateTime FechaHora { get; set; }
        public string? Restricciones { get; set; }         // <-- ahora nullable
        public string NombreReserva { get; set; }
        public string? DNI { get; set; }
        public string? Telefono { get; set; }
        public bool EsTemporal { get; set; }
        public string Estado { get; set; }
        public DateTime CreadoEn { get; set; }
        public DateTime? ActualizadoEn { get; set; }
    }
}
