using System.Collections.Generic;

namespace Shop.Data
{
    public class Order
    {
        public int Id { get; set; }
        public int CustomerId { get; set; }
        public decimal Total { get; set; }
        public string Status { get; set; }
        public IList<OrderLine> Lines { get; set; }
    }

    public class OrderLine
    {
        public int Quantity { get; set; }
        public decimal UnitPrice { get; set; }
    }

    public class Customer
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public bool Active { get; set; }
        public decimal CreditLimit { get; set; }
    }
}
