using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AppWebCentralRestaurante.Models
{
    [Table("Experiencias")]
    public class Experiencia
    {
        [Key]
        public int Id { get; set; }

        [Required, MaxLength(10)]
        public string Codigo { get; set; } // '01','02','03'

        [Required, MaxLength(250)]
        public string Nombre { get; set; } // ej. "MENÚ DEGUSTACIÓN"

        public int? DuracionMinutos { get; set; } // duración aproximada en minutos

        public string Descripcion { get; set; }

        public bool Activa { get; set; } = true;

        // Precio agregado: decimal con 2 decimales.
        // La columna en SQL será DECIMAL(10,2). Inicializa a 0.00 para nuevas entidades.
        [Column(TypeName = "decimal(10,2)")]
        public decimal Precio { get; set; } = 0.00m;

        public DateTime CreadoEn { get; set; } = DateTime.UtcNow;

        // Navegación
        public ICollection<Reserva> Reservas { get; set; }
        public ICollection<RecomendacionLog> RecomendacionesLog { get; set; }
    }
}
