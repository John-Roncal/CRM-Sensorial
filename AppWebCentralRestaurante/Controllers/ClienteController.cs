using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AppWebCentralRestaurante.Data;
using AppWebCentralRestaurante.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Security.Claims;

namespace AppWebCentralRestaurante.Controllers
{
    [Authorize]
    public class ClienteController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly ILogger<ClienteController> _logger;

        public ClienteController(ApplicationDbContext context, ILogger<ClienteController> logger)
        {
            _context = context;
            _logger = logger;
        }

        // GET: /Cliente
        public async Task<IActionResult> Index()
        {
            // Obtener Id del usuario logueado (claim NameIdentifier)
            var claim = User.FindFirst(ClaimTypes.NameIdentifier);
            if (claim == null || !int.TryParse(claim.Value, out var usuarioId))
            {
                // Si no hay usuario, redirigir al login
                return RedirectToAction("Login", "Auth");
            }

            // Traer reservas del usuario (incluimos Experiencia para mostrar nombre)
            var reservas = await _context.Reservas
                .AsNoTracking()
                .Include(r => r.Experiencia)
                .Where(r => r.UsuarioId == usuarioId)
                .OrderByDescending(r => r.FechaHora)
                .ToListAsync();

            // Traer preferencia (si la hay)
            var pref = await _context.Preferencias
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.UsuarioId == usuarioId);

            // Intentar "pretty print" del JSON de preferencias
            string prefPretty = null;
            if (pref != null && !string.IsNullOrWhiteSpace(pref.DatosJson))
            {
                try
                {
                    var obj = JsonSerializer.Deserialize<object>(pref.DatosJson);
                    prefPretty = JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true });
                }
                catch
                {
                    // si no es JSON válido, mostrar tal cual
                    prefPretty = pref.DatosJson;
                }
            }

            var model = new ClienteViewModel
            {
                Reservas = reservas,
                Preferencia = pref,
                PreferenciaJsonPretty = prefPretty
            };

            return View(model);
        }

        // ViewModel mínimo para la vista
        public class ClienteViewModel
        {
            public IEnumerable<Reserva> Reservas { get; set; } = Array.Empty<Reserva>();
            public Preferencia Preferencia { get; set; }
            public string PreferenciaJsonPretty { get; set; }
        }
    }
}
