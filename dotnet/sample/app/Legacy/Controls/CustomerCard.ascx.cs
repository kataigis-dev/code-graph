using System;
using System.Web.UI;

namespace Shop.Legacy.Controls
{
    public partial class CustomerCard : UserControl
    {
        protected void Page_Load(object sender, EventArgs e)
        {
            var name = Session["CustomerName"];
        }
    }
}
