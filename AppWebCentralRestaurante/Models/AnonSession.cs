using System.ComponentModel.DataAnnotations;

namespace AppWebCentralRestaurante.Models
{
    public class AnonSession
    {
        [Key]
        public Guid AnonId { get; set; } = Guid.NewGuid();

        public DateTime CreadoEn { get; set; } = DateTime.UtcNow;

        public DateTime? LastActivity { get; set; }

        public string Estado { get; set; } = "activo";
    }

}
