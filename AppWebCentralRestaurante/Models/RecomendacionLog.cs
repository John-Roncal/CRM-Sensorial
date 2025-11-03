using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AppWebCentralRestaurante.Models
{
    [Table("RecomendacionesLog")]
    public class RecomendacionLog { 
        public int Id { get; set; } 
        public int? UsuarioId { get; set; } 
        public int? ReservaId { get; set; } 
        public int? ExperienciaId { get; set; } 
        public double? Score { get; set; } 
        public string CaracteristicasJson { get; set; } 
        public DateTime CreadoEn { get; set; } }

}
