using System.Collections.Generic;
using Shop.Data;

namespace Shop.Web.Models
{
    public class OrderVm
    {
        public int CustomerId { get; set; }
        public IList<OrderLine> Lines { get; set; } = new List<OrderLine>();
    }
}
