using System;
using System.Web;
using System.Web.Routing;

namespace Shop.Legacy
{
    public class Global : HttpApplication
    {
        protected void Application_Start(object sender, EventArgs e)
        {
            RouteTable.Routes.MapPageRoute("customer-edit", "customers/{id}/edit", "~/Customers/Edit.aspx");
        }

        protected void Session_Start(object sender, EventArgs e)
        {
            Session["Cart"] = null;
        }
    }
}
