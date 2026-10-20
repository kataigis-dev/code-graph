using System.Threading.Tasks;
using NServiceBus;
using Shop.Batch.Jobs;
using Shop.Core.Services;
using Shop.Data;

namespace Shop.Batch
{
    public class PaymentReminderHandler : IHandleMessages<PaymentReminder>
    {
        public Task Handle(PaymentReminder message, IMessageHandlerContext context)
        {
            var order = new OrderRepository().GetById(message.OrderId);
            if (order != null && order.Status == "PendingApproval")
                new EmailNotifier().SendConfirmation(order);
            return Task.CompletedTask;
        }
    }
}
