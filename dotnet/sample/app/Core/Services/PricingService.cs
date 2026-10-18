using System.Collections.Generic;
using System.Linq;
using System.Configuration;
using Shop.Data;

namespace Shop.Core.Services
{
    public class PricingService
    {
        public decimal CalculateTotal(int customerId, IList<OrderLine> lines)
        {
            var gross = lines.Sum(l => l.Quantity * l.UnitPrice);
            var discountThreshold = decimal.Parse(ConfigurationManager.AppSettings["DiscountThreshold"]);
            if (gross >= discountThreshold)
                gross = gross * 0.95m;
            return decimal.Round(gross * 1.22m, 2);
        }
    }

    public class EmailNotifier
    {
        public void SendConfirmation(Order order)
        {
            using (var smtp = new System.Net.Mail.SmtpClient("smtp.local"))
            {
                smtp.Send("noreply@shop.local", "orders@shop.local", "Ordine " + order.Id, "Confermato");
            }
        }
    }
}
