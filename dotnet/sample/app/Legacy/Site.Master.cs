using System;
using System.Web.UI;

namespace Shop.Legacy
{
    public partial class SiteMaster : MasterPage
    {
        protected void Page_Load(object sender, EventArgs e)
        {
            if (Session["User"] == null)
                Response.Redirect("~/Login.aspx");
        }
    }
}
