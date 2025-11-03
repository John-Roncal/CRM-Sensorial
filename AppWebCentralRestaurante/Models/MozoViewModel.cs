using System;
using System.Collections.Generic;

namespace AppWebCentralRestaurante.Models
{
    public class MozoViewModel
    {
        public List<ReservaViewModel> Reservas { get; set; }
    }

    public class ReservaViewModel
    {
        public int Id { get; set; }
        public TimeSpan Hora { get; set; }
        public string CodigoReserva { get; set; }
        public int NumeroPersonas { get; set; }
        public string Alergias { get; set; }
        public string Estado { get; set; }
        public string NombreCliente { get; set; }
        public string NombreExperiencia { get; set; }
        public string DescripcionExperiencia { get; set; }
        public decimal PrecioExperiencia { get; set; }
    }
}
