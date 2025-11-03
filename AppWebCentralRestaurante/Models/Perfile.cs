namespace AppWebCentralRestaurante.Models
{
    public class Perfile
    {
        public int PerfilId { get; set; }
        public int? UsuarioId { get; set; }
        public Guid? AnonId { get; set; }

        // ← permitir nulls
        public string? Q1 { get; set; }
        public string? Q1_Otro { get; set; }
        public string? Q2 { get; set; }
        public string? Q3 { get; set; }

        public bool EstadoPerfilCompleto { get; set; }
        public DateTime CreadoEn { get; set; }
        public DateTime? ActualizadoEn { get; set; }
    }
}
