using System.Linq;
using System.Web;
using Shop.Data;

namespace Shop.Legacy.Infrastructure
{
    /// <summary>Esporta in CSV gli ordini di un cliente.</summary>
    public class ExportHandler : IHttpHandler
    {
        public bool IsReusable => true;

        public void ProcessRequest(HttpContext context)
        {
            var customerId = int.Parse(context.Request.QueryString["customerId"]);
            context.Response.ContentType = "text/csv";
            using (var db = new ShopContext())
            {
                foreach (var o in db.Orders.Where(x => x.CustomerId == customerId))
                    context.Response.Write($"{o.Id};{o.Total};{o.Status}\n");
            }
        }
    }
}
