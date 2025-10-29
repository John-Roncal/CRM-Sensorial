// Models/ReservationDraft.cs
using System;

namespace AppWebCentralRestaurante.Models
{
    public class ReservationDraft
    {
        public string? Dia { get; set; }
        public string? Hora { get; set; }
        public int Personas { get; set; } = 0;

        public int? ExperienciaId { get; set; }    // la experiencia elegida (FK)
        public string? Restricciones { get; set; } // texto, semicolon-separated
        public string Step { get; set; } = "ask_experiencia";

        // Datos iniciales desde el formulario de 3 preguntas
        public string? NombreUsuario { get; set; }
        public bool FromThreeQuestions { get; set; } = false;

        // NUEVO: preferencias detectadas / guardadas (JSON flexible)
        public string? PreferenciasJson { get; set; }

        // NUEVO: contacto
        public string? Dni { get; set; }
        public string? Telefono { get; set; }
    }
}
