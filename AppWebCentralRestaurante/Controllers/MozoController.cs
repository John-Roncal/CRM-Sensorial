using AppWebCentralRestaurante.Data;
using AppWebCentralRestaurante.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System.Linq;
using System.Threading.Tasks;

namespace AppWebCentralRestaurante.Controllers
{
    public class MozoController : Controller
    {
        private readonly ApplicationDbContext _db;
        private readonly ILogger<MozoController> _logger;

        public MozoController(ApplicationDbContext db, ILogger<MozoController> logger)
        {
            _db = db;
            _logger = logger;
        }

        public async Task<IActionResult> Index()
        {
            var model = new MozoViewModel
            {
                Reservas = await _db.GetReservasDelDia()
            };
            return View(model);
        }
    }
}
