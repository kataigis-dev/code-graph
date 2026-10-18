// Stub minimi di System.Web.Mvc: solo cio' che usa l'app d'esempio.
namespace System.Web.Mvc
{
    public abstract class ActionResult { }
    public class ViewResult : ActionResult { }
    public class RedirectToRouteResult : ActionResult { }
    public class HttpNotFoundResult : ActionResult { }
    public class ModelStateDictionary { public bool IsValid => true; public void AddModelError(string key, string message) { } }
    public class TempDataDictionary { public object this[string key] { get => null; set { } } }
    public class HttpSessionStateBase { public object this[string key] { get => null; set { } } }

    public abstract class Controller
    {
        public ModelStateDictionary ModelState => null;
        public TempDataDictionary TempData => null;
        public HttpSessionStateBase Session => null;
        public dynamic ViewBag => null;
        protected ViewResult View() => null;
        protected ViewResult View(object model) => null;
        protected ViewResult View(string viewName, object model) => null;
        protected HttpNotFoundResult HttpNotFound() => null;
        protected RedirectToRouteResult RedirectToAction(string action) => null;
        protected RedirectToRouteResult RedirectToAction(string action, object routeValues) => null;
        protected RedirectToRouteResult RedirectToAction(string action, string controller) => null;
    }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true, AllowMultiple = true)]
    public class AuthorizeAttribute : Attribute { public string Roles { get; set; } }
    public class AllowAnonymousAttribute : Attribute { }
    public class HttpPostAttribute : Attribute { }
    public class HttpGetAttribute : Attribute { }
    public class ValidateAntiForgeryTokenAttribute : Attribute { }
}
