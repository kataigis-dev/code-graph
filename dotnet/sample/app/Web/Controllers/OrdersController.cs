using System.Web.Mvc;
using Shop.Core.Services;
using Shop.Web.Models;

namespace Shop.Web.Controllers
{
    [Authorize(Roles = "Sales")]
    public class OrdersController : Controller
    {
        private readonly IOrderService _orders;

        public OrdersController(IOrderService orders)
        {
            _orders = orders;
        }

        public ActionResult Details(int id)
        {
            var order = _orders.GetOrder(id);
            if (order == null) return HttpNotFound();
            return View(order);
        }

        [HttpGet]
        public ActionResult Create()
        {
            ViewBag.Customers = _orders.ListCustomers();
            return View(new OrderVm());
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult Create(OrderVm vm)
        {
            if (vm.Lines.Count == 0)
                ModelState.AddModelError("Lines", "Inserire almeno una riga d'ordine");
            if (!ModelState.IsValid) return View(vm);

            var cart = Session["Cart"];
            var id = _orders.PlaceOrder(vm.CustomerId, vm.Lines);
            TempData["Message"] = "Ordine creato";
            return RedirectToAction("Details", new { id });
        }
    }
}
