using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AppWebCentralRestaurante.Controllers
{
    [Authorize]
    public class ChatController : Controller
    {
        // GET /Chat
        public async Task<IActionResult> Index()
        {
            return View(); // Views/Chat/Index.cshtml
        }
    }
}
