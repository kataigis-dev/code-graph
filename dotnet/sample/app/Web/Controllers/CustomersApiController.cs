using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;
using System.Web.Http;
using MediatR;
using Shop.Core.Queries;
using Shop.Data;

namespace Shop.Web.Controllers
{
    [RoutePrefix("api/customers")]
    public class CustomersApiController : ApiController
    {
        private readonly IMediator _mediator;

        public CustomersApiController(IMediator mediator)
        {
            _mediator = mediator;
        }

        [HttpGet, Route("{id}")]
        public async Task<IHttpActionResult> Get(int id)
        {
            var customer = await _mediator.Send(new GetCustomerQuery { Id = id });
            return customer == null ? NotFound() : Ok(customer);
        }

        [HttpPost, Route("")]
        public IHttpActionResult Create(CustomerDto dto)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);
            using (var db = new ShopContext())
            {
                db.Customers.Add(new Customer { Name = dto.Name, CreditLimit = dto.CreditLimit, Active = true });
                db.SaveChanges();
            }
            return Ok();
        }
    }

    public class CustomerDto
    {
        [Required(ErrorMessage = "Il nome e' obbligatorio"), StringLength(100)]
        public string Name { get; set; }

        [Range(0, 50000, ErrorMessage = "Fido tra 0 e 50.000")]
        public decimal CreditLimit { get; set; }
    }
}
