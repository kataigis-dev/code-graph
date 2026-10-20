// Stub minimi di WCF, Windows Service, Quartz, Hangfire, NServiceBus, MediatR, SignalR, OWIN e Web API:
// solo cio' che usa l'app d'esempio. Contano i namespace e i nomi dei tipi, non l'assembly.
namespace System.ServiceModel
{
    [AttributeUsage(AttributeTargets.Interface | AttributeTargets.Class)] public class ServiceContractAttribute : Attribute { }
    [AttributeUsage(AttributeTargets.Method)] public class OperationContractAttribute : Attribute { }
    public class FaultException : Exception { public FaultException(string reason) : base(reason) { } }
    public class ServiceHost : IDisposable { public ServiceHost(Type serviceType) { } public void Open() { } public void Dispose() { } }
}

namespace System.Security.Permissions
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
    public class PrincipalPermissionAttribute : Attribute
    {
        public PrincipalPermissionAttribute(SecurityAction action) { }
        public string Role { get; set; }
    }
}

namespace System.ServiceProcess
{
    public class ServiceBase
    {
        protected virtual void OnStart(string[] args) { }
        protected virtual void OnStop() { }
    }
}

namespace Quartz
{
    public interface IJobExecutionContext { }
    public interface IJob { System.Threading.Tasks.Task Execute(IJobExecutionContext context); }
}

namespace Hangfire
{
    using System;
    using System.Linq.Expressions;
    public static class Cron { public static string Daily() => "0 0 * * *"; }
    public static class RecurringJob { public static void AddOrUpdate(string recurringJobId, Expression<Action> methodCall, string cronExpression) { } }
    public static class BackgroundJob { public static string Enqueue(Expression<Action> methodCall) => null; }
}

namespace NServiceBus
{
    using System.Threading.Tasks;
    public interface IMessageHandlerContext { }
    public interface IMessageSession { Task Send(object message); Task Publish(object message); }
    public interface IHandleMessages<T> { Task Handle(T message, IMessageHandlerContext context); }
}

namespace MediatR
{
    using System.Threading;
    using System.Threading.Tasks;
    public interface IRequest<out TResponse> { }
    public interface IRequestHandler<in TRequest, TResponse> where TRequest : IRequest<TResponse>
    {
        Task<TResponse> Handle(TRequest request, CancellationToken cancellationToken);
    }
    public interface IMediator { Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default); }
}

namespace Microsoft.AspNet.SignalR
{
    using System;
    public class Hub { }
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)] public class AuthorizeAttribute : Attribute { public string Roles { get; set; } }
}

namespace Owin
{
    public interface IAppBuilder { IAppBuilder Use(object middleware, params object[] args); }
}

namespace System.Web.Http
{
    public interface IHttpActionResult { }
    public class ModelStateDictionary { public bool IsValid => true; }
    public abstract class ApiController
    {
        public ModelStateDictionary ModelState => null;
        protected IHttpActionResult Ok() => null;
        protected IHttpActionResult Ok<T>(T content) => null;
        protected IHttpActionResult BadRequest(ModelStateDictionary modelState) => null;
        protected IHttpActionResult NotFound() => null;
    }
    [AttributeUsage(AttributeTargets.Class)] public class RoutePrefixAttribute : Attribute { public RoutePrefixAttribute(string prefix) { } }
    [AttributeUsage(AttributeTargets.Method)] public class RouteAttribute : Attribute { public RouteAttribute(string template) { } }
    public class HttpPostAttribute : Attribute { }
    public class HttpGetAttribute : Attribute { }
}
