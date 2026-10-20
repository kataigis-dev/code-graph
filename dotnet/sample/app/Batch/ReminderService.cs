using System.ServiceModel;
using System.ServiceProcess;
using System.Timers;
using Shop.Batch.Jobs;

namespace Shop.Batch
{
    /// <summary>Servizio Windows: ogni ora invia i solleciti; espone un endpoint WCF per lanciare i job a mano.</summary>
    public class ReminderService : ServiceBase
    {
        private readonly Timer _timer = new Timer(3600000);
        private ServiceHost _host;

        protected override void OnStart(string[] args)
        {
            _timer.Elapsed += OnTick;
            _timer.Start();
            _host = new ServiceHost(typeof(JobControlService));
            _host.Open();
        }

        protected override void OnStop()
        {
            _timer.Stop();
            _host.Dispose();
        }

        private void OnTick(object sender, ElapsedEventArgs e)
        {
            new ReminderJob(null).SendReminders();
        }
    }

    [ServiceContract]
    public interface IJobControl
    {
        [OperationContract]
        void RunInvoicing();
    }

    public class JobControlService : IJobControl
    {
        public void RunInvoicing() => new InvoiceJob().Run();
    }
}
