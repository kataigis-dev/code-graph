// Stub minimi di System.Web (Web Forms, asmx, routing): solo cio' che usa l'app d'esempio.
namespace System.Web
{
    public class HttpContext
    {
        public static HttpContext Current => null;
        public HttpRequest Request => null;
        public HttpResponse Response => null;
        public System.Security.Principal.IPrincipal User => null;
    }
    public class HttpResponse
    {
        public string ContentType { get; set; }
        public void Write(string s) { }
        public void Redirect(string url) { }
        public void Redirect(string url, bool endResponse) { }
    }
    public class HttpServerUtility { public void Transfer(string path) { } }
    public class HttpRequest
    {
        public string Path => null;
        public System.Collections.Specialized.NameValueCollection QueryString => null;
        public string this[string key] => null;
    }
    public class HttpException : Exception { public HttpException(int httpCode, string message) : base(message) { } }
    public interface IHttpHandler { void ProcessRequest(HttpContext context); bool IsReusable { get; } }
    public interface IHttpModule { void Init(HttpApplication context); void Dispose(); }
    public class HttpApplication
    {
        public SessionState.HttpSessionState Session => null;
        public HttpContext Context => null;
        public event EventHandler BeginRequest { add { } remove { } }
    }
}

namespace System.Web.SessionState
{
    public class HttpSessionState { public object this[string key] { get => null; set { } } }
}

namespace System.Web.UI
{
    public class StateBag { public object this[string key] { get => null; set { } } }

    public class Control
    {
        protected StateBag ViewState => null;
        public SessionState.HttpSessionState Session => null;
        public HttpResponse Response => null;
        public HttpRequest Request => null;
        public HttpServerUtility Server => null;
        protected virtual void OnInit(EventArgs e) { }
        protected virtual void OnLoad(EventArgs e) { }
    }

    public class TemplateControl : Control { }
    public class Page : TemplateControl { public bool IsPostBack => false; public bool IsValid => true; }
    public class UserControl : TemplateControl { }
    public class MasterPage : UserControl { }
}

namespace System.Web.UI.WebControls
{
    public class WebControl : UI.Control { }
    public class TextBox : WebControl { public string Text { get; set; } }
    public class Label : WebControl { public string Text { get; set; } }
    public class Button : WebControl { public event EventHandler Click { add { } remove { } } }
    public class DropDownList : WebControl { public string SelectedValue => null; }
}

namespace System.Web.Services
{
    public class WebService { }
    [AttributeUsage(AttributeTargets.Class)] public class WebServiceAttribute : Attribute { public string Namespace { get; set; } }
    [AttributeUsage(AttributeTargets.Method)] public class WebMethodAttribute : Attribute { }
}

namespace System.Web.Routing
{
    public class RouteCollection { public object MapPageRoute(string routeName, string routeUrl, string physicalFile) => null; }
    public static class RouteTable { public static RouteCollection Routes => null; }
}
