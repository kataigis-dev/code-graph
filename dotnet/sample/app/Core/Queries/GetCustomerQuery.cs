using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Shop.Data;

namespace Shop.Core.Queries
{
    public class GetCustomerQuery : IRequest<Customer>
    {
        public int Id { get; set; }
    }

    public class GetCustomerHandler : IRequestHandler<GetCustomerQuery, Customer>
    {
        public Task<Customer> Handle(GetCustomerQuery request, CancellationToken cancellationToken)
        {
            using (var db = new ShopContext())
            {
                var customer = db.Customers.Find(request.Id);
                // i clienti disattivati non sono visibili dall'API
                return Task.FromResult(customer != null && customer.Active ? customer : null);
            }
        }
    }
}
