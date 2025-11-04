using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;

namespace AppWebCentralRestaurante.Controllers
{
    [Authorize(Roles = "Chef")]
    public class ChefController : Controller
    {
        // GET: ChefController
        public IActionResult Index()
        {
            return View();
        }

        // GET: ChefController/Details/5
        public ActionResult Details(int id)
        {
            return View();
        }

        // GET: ChefController/Create
        public ActionResult Create()
        {
            return View();
        }

        // POST: ChefController/Create
        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult Create(IFormCollection collection)
        {
            try
            {
                return RedirectToAction(nameof(Index));
            }
            catch
            {
                return View();
            }
        }

        // GET: ChefController/Edit/5
        public ActionResult Edit(int id)
        {
            return View();
        }

        // POST: ChefController/Edit/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult Edit(int id, IFormCollection collection)
        {
            try
            {
                return RedirectToAction(nameof(Index));
            }
            catch
            {
                return View();
            }
        }

        // GET: ChefController/Delete/5
        public ActionResult Delete(int id)
        {
            return View();
        }

        // POST: ChefController/Delete/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult Delete(int id, IFormCollection collection)
        {
            try
            {
                return RedirectToAction(nameof(Index));
            }
            catch
            {
                return View();
            }
        }
    }
}
