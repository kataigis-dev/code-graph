using System.Configuration;
using System.Data;
using System.Data.SqlClient;
using System.Threading.Tasks;
using Quartz;

namespace Shop.Batch.Jobs
{
    /// <summary>Fatturazione degli ordini confermati, schedulata con Quartz e con Hangfire.</summary>
    public class InvoiceJob : IJob
    {
        public Task Execute(IJobExecutionContext context)
        {
            Run();
            return Task.CompletedTask;
        }

        public void Run()
        {
            var batchSize = int.Parse(ConfigurationManager.AppSettings["InvoiceBatchSize"]);
            using (var cmd = new SqlCommand("usp_InvoiceOrders") { CommandType = CommandType.StoredProcedure })
            {
                cmd.Parameters.AddWithValue("@maxOrders", batchSize);
                cmd.ExecuteNonQuery();
            }
        }
    }
}
