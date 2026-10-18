using System;
using System.Collections.Generic;
using Shop.Data;

namespace Shop.Core.Services
{
    public interface IOrderService
    {
        Order GetOrder(int id);
        IList<Customer> ListCustomers();
        int PlaceOrder(int customerId, IList<OrderLine> lines);
    }

    public class OrderService : IOrderService
    {
        private readonly OrderRepository _repo;
        private readonly PricingService _pricing;
        private readonly EmailNotifier _mail;

        public Order GetOrder(int id) => _repo.GetById(id);

        public IList<Customer> ListCustomers() => _repo.ListCustomers();

        public int PlaceOrder(int customerId, IList<OrderLine> lines)
        {
            var total = _pricing.CalculateTotal(customerId, lines);
            var credit = _repo.GetCreditLimit(customerId);
            if (total > credit)
                throw new InvalidOperationException("Fido cliente superato");

            var order = new Order { CustomerId = customerId, Total = total, Status = total > 10000 ? "PendingApproval" : "Confirmed" };
            _repo.Save(order);
            _mail.SendConfirmation(order);
            return order.Id;
        }
    }
}
