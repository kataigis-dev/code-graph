using Hangfire;
using Shop.Batch.Jobs;

namespace Shop.Batch
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            RecurringJob.AddOrUpdate("fatturazione-notturna", () => new InvoiceJob().Run(), Cron.Daily());
            BackgroundJob.Enqueue(() => new ReminderJob(null).SendReminders());
            return 0;
        }
    }
}
