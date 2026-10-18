using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;

namespace Shop.Data
{
    public class OrderRepository
    {
        private readonly ShopContext _db = new ShopContext();

        public Order GetById(int id)
        {
            return _db.Orders.FirstOrDefault(o => o.Id == id);
        }

        public IList<Customer> ListCustomers()
        {
            return _db.Customers.Where(c => c.Active).ToList();
        }

        public decimal GetCreditLimit(int customerId)
        {
            using (var cmd = new SqlCommand("SELECT CreditLimit FROM dbo.Customers WHERE Id = @id"))
            {
                cmd.Parameters.AddWithValue("@id", customerId);
                return (decimal)cmd.ExecuteScalar();
            }
        }

        public void Save(Order order)
        {
            _db.Orders.Add(order);
            _db.SaveChanges();
        }
    }
}
