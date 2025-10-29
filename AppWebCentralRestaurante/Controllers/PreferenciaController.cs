using System;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using AppWebCentralRestaurante.Data;
using AppWebCentralRestaurante.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AppWebCentralRestaurante.Controllers
{
    [Authorize] // requiere usuario autenticado
    public class PreferenciasController : Controller
    {
        private readonly CentralContext _context;
        private readonly ILogger<PreferenciasController> _logger;

        public PreferenciasController(CentralContext context, ILogger<PreferenciasController> logger)
        {
            _context = context;
            _logger = logger;
        }

        /// <summary>
        /// POST /Preferencias/Guardar
        /// Recibe JSON { datosJson: "..."} y guarda/actualiza la fila Preferencias para el usuario autenticado.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Guardar([FromForm] string datosJson)
        {
            // obtener id usuario desde claims
            var claim = User.FindFirst(ClaimTypes.NameIdentifier);
            if (claim == null || !int.TryParse(claim.Value, out var usuarioId))
                return Unauthorized();

            if (string.IsNullOrWhiteSpace(datosJson))
                return BadRequest("datosJson vacío.");

            try
            {
                // opcional: validar que datosJson sea JSON válido
                try
                {
                    using var doc = JsonDocument.Parse(datosJson);
                }
                catch (Exception)
                {
                    return BadRequest("datosJson no es un JSON válido.");
                }

                var pref = await _context.Preferencias.FirstOrDefaultAsync(p => p.UsuarioId == usuarioId);
                if (pref == null)
                {
                    pref = new Preferencia
                    {
                        UsuarioId = usuarioId,
                        DatosJson = datosJson,
                        CreadoEn = DateTime.UtcNow
                    };
                    _context.Preferencias.Add(pref);
                }
                else
                {
                    pref.DatosJson = datosJson;
                    pref.ActualizadoEn = DateTime.UtcNow;
                    _context.Preferencias.Update(pref);
                }

                await _context.SaveChangesAsync();
                return Ok(new { ok = true, message = "Preferencias guardadas." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error guardando preferencias para usuario {u}", usuarioId);
                return StatusCode(500, "Error guardando preferencias.");
            }
        }

        /// <summary>
        /// GET /Preferencias/MisPreferencias
        /// Devuelve las preferencias del usuario autenticado.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> MisPreferencias()
        {
            var claim = User.FindFirst(ClaimTypes.NameIdentifier);
            if (claim == null || !int.TryParse(claim.Value, out var usuarioId))
                return Unauthorized();

            var pref = await _context.Preferencias.AsNoTracking().FirstOrDefaultAsync(p => p.UsuarioId == usuarioId);
            if (pref == null) return NotFound();

            return Ok(new { ok = true, datosJson = pref.DatosJson });
        }

        /// <summary>
        /// POST /Preferencias/Eliminar
        /// Borra la preferencia del usuario (opcional).
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Eliminar()
        {
            var claim = User.FindFirst(ClaimTypes.NameIdentifier);
            if (claim == null || !int.TryParse(claim.Value, out var usuarioId))
                return Unauthorized();

            var pref = await _context.Preferencias.FirstOrDefaultAsync(p => p.UsuarioId == usuarioId);
            if (pref == null) return NotFound();

            _context.Preferencias.Remove(pref);
            await _context.SaveChangesAsync();
            return Ok(new { ok = true });
        }
    }
}
