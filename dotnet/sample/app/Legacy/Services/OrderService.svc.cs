using System.Security.Permissions;
using System.ServiceModel;
using Shop.Data;

namespace Shop.Legacy.Services
{
    [ServiceContract]
    public interface IOrderWcfService
    {
        [OperationContract]
        decimal GetCreditLimit(int customerId);

        [OperationContract]
        void CancelOrder(int orderId);
    }

    public class OrderWcfService : IOrderWcfService
    {
        public decimal GetCreditLimit(int customerId) => new OrderRepository().GetCreditLimit(customerId);

        [PrincipalPermission(SecurityAction.Demand, Role = "Admin")]
        public void CancelOrder(int orderId)
        {
            using (var db = new ShopContext())
            {
                var order = db.Orders.Find(orderId);
                if (order.Status == "Invoiced")
                    throw new FaultException("Ordine gia' fatturato: non si puo' annullare");
                order.Status = "Cancelled";
                db.SaveChanges();
            }
        }
    }
}
