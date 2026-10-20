using Microsoft.AspNet.SignalR;
using Shop.Data;

namespace Shop.Web.Hubs
{
    [Authorize(Roles = "Sales")]
    public class OrdersHub : Hub
    {
        public Order GetOrder(int id) => new OrderRepository().GetById(id);
    }
}
