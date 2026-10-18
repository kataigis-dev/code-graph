// Stub minimi di Entity Framework 6, ADO.NET e System.Configuration: solo cio' che usa l'app d'esempio.
namespace System.Data.Entity
{
    public enum EntityState { Unchanged, Added, Deleted, Modified }
    public class DbEntityEntry<T> where T : class { public EntityState State { get; set; } }

    public class DbSet<T> : Collections.Generic.IEnumerable<T> where T : class
    {
        public T Add(T entity) => entity;
        public T Remove(T entity) => entity;
        public T Find(params object[] keys) => null;
        public Collections.Generic.IEnumerator<T> GetEnumerator() => throw new NotSupportedException();
        Collections.IEnumerator Collections.IEnumerable.GetEnumerator() => throw new NotSupportedException();
    }

    public class DbContext
    {
        public DbSet<T> Set<T>() where T : class => null;
        public DbEntityEntry<T> Entry<T>(T entity) where T : class => null;
        public int SaveChanges() => 0;
    }
}

namespace System.Data.SqlClient
{
    public class SqlParameterCollection { public object AddWithValue(string name, object value) => null; }
    public class SqlCommand : IDisposable
    {
        public SqlCommand(string text) { }
        public SqlParameterCollection Parameters => null;
        public object ExecuteScalar() => null;
        public void Dispose() { }
    }
}

namespace System.Configuration
{
    public static class ConfigurationManager { public static Collections.Specialized.NameValueCollection AppSettings => null; }
}
