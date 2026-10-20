using System.Linq;
using NServiceBus;
using Shop.Data;

namespace Shop.Batch.Jobs
{
    public class PaymentReminder
    {
        public int OrderId { get; set; }
    }

    public class ReminderJob
    {
        private readonly IMessageSession _bus;

        public ReminderJob(IMessageSession bus)
        {
            _bus = bus;
        }

        public void SendReminders()
        {
            using (var db = new ShopContext())
            {
                foreach (var order in db.Orders.Where(o => o.Status == "PendingApproval"))
                    _bus.Send(new PaymentReminder { OrderId = order.Id });
            }
        }
    }
}
