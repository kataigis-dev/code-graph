using System.Web.Services;
using Shop.Data;

namespace Shop.Legacy.Services
{
    [WebService(Namespace = "http://shop.local/")]
    public class Pricing : WebService
    {
        [WebMethod]
        public decimal CreditLimit(int customerId)
        {
            return new OrderRepository().GetCreditLimit(customerId);
        }

        public void NotExposed()
        {
        }
    }
}
