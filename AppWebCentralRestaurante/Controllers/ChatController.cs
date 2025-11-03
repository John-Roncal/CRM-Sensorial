using Microsoft.AspNetCore.Mvc;

namespace AppWebCentralRestaurante.Controllers
{
    public class ChatController : Controller
    {
        // GET /Chat
        public IActionResult Index()
        {
            return View(); // Views/Chat/Index.cshtml
        }
    }
}
