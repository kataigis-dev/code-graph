using System.Data.Entity;

namespace Shop.Data
{
    public class ShopContext : DbContext
    {
        public DbSet<Order> Orders { get; set; }
        public DbSet<Customer> Customers { get; set; }
    }
}
