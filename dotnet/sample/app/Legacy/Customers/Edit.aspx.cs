using System;
using System.Web.UI;
using Shop.Data;

namespace Shop.Legacy.Customers
{
    public partial class Edit : Page
    {
        private readonly ShopContext _db = new ShopContext();

        protected override void OnInit(EventArgs e)
        {
            base.OnInit(e);
            btnDeactivate.Click += btnDeactivate_Click;
        }

        protected void Page_Load(object sender, EventArgs e)
        {
            if (!IsPostBack)
                ViewState["CustomerId"] = Request.QueryString["id"];
        }

        protected void btnSave_Click(object sender, EventArgs e)
        {
            if (!IsValid) return;
            var id = int.Parse((string)ViewState["CustomerId"]);
            var customer = _db.Customers.Find(id);
            customer.Name = txtName.Text;
            _db.SaveChanges();
            Response.Redirect("~/Customers/Edit.aspx?id=" + id);
        }

        protected void btnDeactivate_Click(object sender, EventArgs e)
        {
            var customer = _db.Customers.Find(int.Parse((string)ViewState["CustomerId"]));
            customer.Active = false;
            _db.SaveChanges();
        }

        protected void ddlCustomers_SelectedIndexChanged(object sender, EventArgs e)
        {
            Server.Transfer("Edit.aspx?id=" + ddlCustomers.SelectedValue);
        }
    }
}
