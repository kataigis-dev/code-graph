using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynGraph;

/// <summary>
/// Passata 6: entry point che non sono action o pagine. Ognuno diventa una Route con il suo flavor:
///   wcf          operazioni [OperationContract] (quelle ospitate da un .svc le crea la passata Web Forms, con l'URL)
///   service      Windows Service (ServiceBase: OnStart, OnStop, ...)
///   batch        Main di un progetto console; desktop se il progetto e' un'applicazione Windows
///   job          Quartz (IJob.Execute) e Hangfire (BackgroundJob/RecurringJob con il metodo della lambda)
///   timer        System.Timers.Timer.Elapsed e System.Threading.Timer
///   message      handler di messaggi (NServiceBus, MassTransit, Rebus)
///   httpmodule   IHttpModule: Init e gli eventi dell'applicazione a cui si iscrive (BeginRequest, ...)
///   httphandler  IHttpHandler registrati nel web.config (gli .ashx li crea la passata Web Forms)
///   signalr      metodi pubblici degli hub
///   owin         Startup.Configuration(IAppBuilder)
/// MediatR non e' un entry point: Send/Publish diventano archi CALLS verso gli handler (via = "mediatr").
/// Chi pianifica un job o un timer ha un arco SCHEDULES verso la sua rotta; chi invia un messaggio un arco SENDS_MESSAGE.
/// </summary>
public sealed partial class Extractor
{
    private static readonly string[] ServiceLifecycle = { "OnStart", "OnStop", "OnPause", "OnContinue", "OnShutdown", "OnCustomCommand", "OnPowerEvent", "OnSessionChange" };

    private static readonly Dictionary<string, string> MessageHandlerInterfaces = new()
    {
        ["NServiceBus.IHandleMessages<T>"] = "Handle", ["MassTransit.IConsumer<TMessage>"] = "Consume",
        ["Rebus.Handlers.IHandleMessages<TMessage>"] = "Handle",
    };
    private static readonly string[] MessageBusNamespaces = { "NServiceBus", "MassTransit", "Rebus" };
    private static readonly HashSet<string> MediatrHandlerInterfaces = new()
    {
        "MediatR.IRequestHandler<TRequest, TResponse>", "MediatR.IRequestHandler<TRequest>", "MediatR.INotificationHandler<TNotification>",
    };

    private readonly Dictionary<string, List<string>> _mediatrHandlers = new();         // tipo della richiesta -> metodi Handle
    private readonly List<PendingJob> _pendingJobs = new();
    private readonly List<PendingTimer> _pendingTimers = new();
    private readonly List<(string TypeId, string Event, string Handler)> _pendingAppEvents = new();
    private readonly List<(string From, string MessageType, int Line)> _pendingMessages = new();
    private readonly HashSet<string> _hostedServices = new();                            // tipi gia' esposti da un .svc

    private sealed record PendingJob(string From, string Handler, string Api, string? Id, string? Schedule, int Line);
    private sealed record PendingTimer(string From, string Handler, string Kind, int Line);

    private void PassEntryPoints()
    {
        var handlersConfig = _units.ToDictionary(u => u.Project.Name, u => LoadHandlerConfig(u.Project));
        var messageRoutes = new Dictionary<string, List<string>>();
        int before = _g.Nodes.Values.Count(n => n.Kind == "Route");

        foreach (var (p, comp) in _units)
            if (comp.GetEntryPoint(default) is { } main && InSource(main) && Id(main.Norm()) is { } mid)
            {
                var flavor = comp.Options.OutputKind == OutputKind.WindowsApplication ? "desktop" : "batch";
                AddRoute(p.Name, flavor, ["MAIN"], p.Name, p.Name, "Main", [], [mid], "PRJ:" + p.Name, url: false);
            }

        foreach (var (p, ts) in _types)
        {
            if (ts.TypeKind != TypeKind.Class || ts.IsAbstract || Id(ts) is not { } tid) continue;
            var auth = ts.Authorization();

            if (!_hostedServices.Contains(tid))
                foreach (var (op, impl) in WcfOperations(ts))
                    if (Id(impl.Norm()) is { } oid)
                        AddRoute(p.Name, "wcf", ["WCF"], $"{ts.Name}/{op}", ts.Name, op, auth.Concat(impl.Authorization()).Distinct().ToList(),
                                 [oid], tid, url: false);

            if (Derives(ts, "System.ServiceProcess.ServiceBase"))
                Single(p, ts, "service", "SERVICE", MethodIds(ts, m => m.IsOverride && ServiceLifecycle.Contains(m.Name)));

            if (Implements(ts, "Quartz.IJob"))
                Single(p, ts, "job", "JOB", MethodIds(ts, m => m.Name == "Execute"));

            foreach (var iface in ts.AllInterfaces)
            {
                if (!MessageHandlerInterfaces.TryGetValue(iface.OriginalDefinition.ToDisplayString(), out var methodName)) continue;
                var member = iface.GetMembers(methodName).OfType<IMethodSymbol>().FirstOrDefault();
                if (member is null || ts.FindImplementationForInterfaceMember(member) is not IMethodSymbol impl || Id(impl.Norm()) is not { } hid) continue;
                var message = iface.TypeArguments[0];
                var route = AddRoute(p.Name, "message", ["MESSAGE"], message.Name, ts.Name, methodName, auth, [hid], tid, url: false);
                route["message"] = message.ToDisplayString();
                messageRoutes.Bucket(message.ToDisplayString()).Add(route.Id);
            }

            if (Implements(ts, "System.Web.IHttpModule"))
            {
                var events = _pendingAppEvents.Where(e => e.TypeId == tid).ToList();
                var route = Single(p, ts, "httpmodule", "MODULE", MethodIds(ts, m => m.Name == "Init").Concat(events.Select(e => e.Handler)).Distinct().ToList());
                if (route is not null && events.Count > 0) route["events"] = events.Select(e => e.Event).Distinct().ToList();
                if (route is not null && handlersConfig[p.Name].Modules.Contains(ts.ToDisplayString())) route["registeredIn"] = "web.config";
            }

            if ((Implements(ts, "System.Web.IHttpHandler") || Implements(ts, "System.Web.IHttpAsyncHandler")) && !_pageByType.ContainsKey(tid))
            {
                var mapping = handlersConfig[p.Name].Handlers.FirstOrDefault(h => h.Type == ts.ToDisplayString());
                var verbs = mapping.Verb is { Length: > 0 } v && v != "*" ? v.Split(',').Select(x => x.Trim().ToUpperInvariant()).ToList() : ["ANY"];
                var process = MethodIds(ts, m => m.Name is "ProcessRequest" or "BeginProcessRequest");
                if (process.Count > 0)
                {
                    var handlerAuth = mapping.Path is null ? [] : AuthFor(_authScopes.GetValueOrDefault(p.Name), mapping.Path);
                    var route = AddRoute(p.Name, "httphandler", verbs, mapping.Path ?? ts.Name, ts.Name, "ProcessRequest", handlerAuth, process, tid,
                                         url: mapping.Path is not null);
                    if (mapping.Path is null) route["unresolved"] = "percorso non trovato nel web.config: registrazione altrove o handler non usato";
                }
            }

            if (Derives(ts, "Microsoft.AspNet.SignalR.Hub") || Derives(ts, "Microsoft.AspNetCore.SignalR.Hub"))
            {
                var hub = ts.AttrString("HubName") ?? ts.Name;
                foreach (var m in MethodsInHierarchy(ts).Where(m => m is { DeclaredAccessibility: Accessibility.Public, IsStatic: false }
                                                                    && !IsHubPlumbing(m)))
                    if (Id(m.Norm()) is { } hmid)
                        AddRoute(p.Name, "signalr", ["HUB"], $"{hub}/{m.AttrString("HubMethodName") ?? m.Name}", hub, m.Name,
                                 auth.Concat(m.Authorization()).Distinct().ToList(), [hmid], tid, url: false);
            }

            var owin = MethodIds(ts, m => m.Name == "Configuration" && m.Parameters is [{ Type: var t }] && t.ToDisplayString() == "Owin.IAppBuilder");
            if (owin.Count > 0) Single(p, ts, "owin", "OWIN", owin);
        }

        foreach (var j in _pendingJobs)
        {
            var (project, owner) = OwnerOf(j.Handler);
            var route = AddRoute(project, "job", ["JOB"], j.Id ?? Short(j.Handler), owner, j.Api, [], [j.Handler], j.From, url: false);
            if (j.Schedule is not null) route["schedule"] = j.Schedule;
            route["scheduler"] = "Hangfire";
            _g.Edge(j.From, route.Id, "SCHEDULES", j.Line);
        }
        foreach (var t in _pendingTimers)
        {
            var (project, owner) = OwnerOf(t.Handler);
            var route = AddRoute(project, "timer", ["TIMER"], Short(t.Handler), owner, t.Kind, [], [t.Handler], t.From, url: false);
            _g.Edge(t.From, route.Id, "SCHEDULES", t.Line);
        }
        foreach (var (from, type, line) in _pendingMessages)
            foreach (var rid in messageRoutes.GetValueOrDefault(type) ?? [])
                _g.Edge(from, rid, "SENDS_MESSAGE", line);

        Info($"   {_g.Nodes.Values.Count(n => n.Kind == "Route") - before} entry point non HTTP (servizi, batch, job, messaggi, moduli, hub)");
    }

    private GraphNode? Single(Project p, INamedTypeSymbol ts, string flavor, string verb, List<string> handlers) =>
        handlers.Count == 0 ? null : AddRoute(p.Name, flavor, [verb], ts.Name, ts.Name, flavor, ts.Authorization(), handlers, Id(ts)!, url: false);

    /// <summary>Operazioni WCF del tipo: dai contratti [ServiceContract] che implementa, o dal tipo stesso se e' il contratto.</summary>
    private IEnumerable<(string Name, IMethodSymbol Impl)> WcfOperations(INamedTypeSymbol ts)
    {
        foreach (var contract in ts.AllInterfaces.Where(i => i.HasAttr("ServiceContract")))
            foreach (var op in contract.GetMembers().OfType<IMethodSymbol>().Where(m => m.HasAttr("OperationContract")))
                if (ts.FindImplementationForInterfaceMember(op) is IMethodSymbol impl && InSource(impl))
                    yield return (op.AttrString("OperationContract") ?? op.Name, impl);
        if (ts.HasAttr("ServiceContract"))
            foreach (var m in ts.GetMembers().OfType<IMethodSymbol>().Where(m => m.HasAttr("OperationContract")))
                yield return (m.Name, m);
    }

    private static bool Derives(INamedTypeSymbol t, string baseName)
    {
        for (var b = t.BaseType; b is not null; b = b.BaseType)
            if (b.OriginalDefinition.ToDisplayString() is var s && (s == baseName || s.StartsWith(baseName + "<", StringComparison.Ordinal))) return true;
        return false;
    }

    private static bool Implements(INamedTypeSymbol t, string iface) => t.AllInterfaces.Any(i => i.OriginalDefinition.ToDisplayString() == iface);

    private static bool IsHubPlumbing(IMethodSymbol m) =>
        m.MethodKind != MethodKind.Ordinary || m.IsOverride || m.Name is "Dispose" or "OnConnected" or "OnDisconnected" or "OnReconnected";

    private (string Project, string Owner) OwnerOf(string methodId) =>
        _g.Get(methodId) is { } n ? ((string?)n["project"] ?? "?", ((string?)n["containingType"])?.Split('.').Last() ?? "?") : ("?", "?");

    private string Short(string methodId) => _g.Get(methodId)?.Name ?? methodId;

    // ------------------------------------------------------------------ registrazioni raccolte nelle passate 1 e 3

    /// <summary>Passata 1: handler MediatR per tipo di richiesta.</summary>
    private void RegisterMediatrHandlers(INamedTypeSymbol ts)
    {
        foreach (var iface in ts.AllInterfaces.Where(i => MediatrHandlerInterfaces.Contains(i.OriginalDefinition.ToDisplayString())))
            if (iface.GetMembers("Handle").OfType<IMethodSymbol>().FirstOrDefault() is { } member
                && ts.FindImplementationForInterfaceMember(member) is IMethodSymbol impl && Id(impl.Norm()) is { } hid)
                _mediatrHandlers.Bucket(iface.TypeArguments[0].ToDisplayString()).Add(hid);
    }

    /// <summary>Passata 3: Hangfire, MediatR e bus di messaggi visti in una chiamata.</summary>
    private void OnDispatchInvocation(BodyWork w, GraphNode n, InvocationExpressionSyntax inv, IMethodSymbol target)
    {
        var type = target.ContainingType?.OriginalDefinition.ToDisplayString() ?? "";
        var ns = target.ContainingNamespace?.ToDisplayString() ?? "";

        if (type.StartsWith("Hangfire.", StringComparison.Ordinal)
            && target.Name is "Enqueue" or "Schedule" or "AddOrUpdate" or "ContinueJobWith" or "ContinueWith")
        {
            var args = inv.ArgumentList.Arguments;
            var lambdaIndex = args.ToList().FindIndex(a => a.Expression is LambdaExpressionSyntax);
            if (lambdaIndex < 0 || ((LambdaExpressionSyntax)args[lambdaIndex].Expression).ExpressionBody is not InvocationExpressionSyntax call
                || w.Model.SymbolOf(call) is not IMethodSymbol job || !InSource(job.Norm()) || Id(job.Norm()) is not { } jid) return;
            var id = target.Name == "AddOrUpdate" ? inv.LiteralArg(0) : null;
            var schedule = lambdaIndex + 1 < args.Count ? Text.OneLine(args[lambdaIndex + 1].Expression.ToString(), 120) : null;
            _pendingJobs.Add(new PendingJob(w.Id, jid, target.Name, id, schedule, inv.Line()));
        }
        else if (ns.StartsWith("MediatR", StringComparison.Ordinal) && target.Name is "Send" or "Publish"
                 && inv.ArgumentList.Arguments.FirstOrDefault() is { } arg && w.Model.GetTypeInfo(arg.Expression).Type is { } request)
        {
            foreach (var h in _mediatrHandlers.GetValueOrDefault(request.OriginalDefinition.ToDisplayString()) ?? [])
                _g.Edge(w.Id, h, "CALLS", inv.Line())["via"] = "mediatr";
            if (!_mediatrHandlers.ContainsKey(request.OriginalDefinition.ToDisplayString()))
                n.AddDistinct("unresolved_calls", $"MediatR {target.Name}({request.Name}): handler non trovato nella solution", 50);
        }
        else if (MessageBusNamespaces.Any(b => ns.StartsWith(b, StringComparison.Ordinal)) && target.Name is "Send" or "Publish" or "SendLocal"
                 && inv.ArgumentList.Arguments.FirstOrDefault() is { } msg && w.Model.GetTypeInfo(msg.Expression).Type is { } messageType)
        {
            n.AddDistinct("messages_sent", messageType.ToDisplayString());
            _pendingMessages.Add((w.Id, messageType.ToDisplayString(), inv.Line()));
        }
    }

    /// <summary>Passata 3: eventi di timer e dell'applicazione ASP.NET (timer.Elapsed += OnTick, context.BeginRequest += ...).</summary>
    private bool OnEventSubscription(BodyWork w, IEventSymbol ev, string handlerId, int line)
    {
        var owner = ev.ContainingType.OriginalDefinition.ToDisplayString();
        if (owner == "System.Timers.Timer" && ev.Name == "Elapsed") _pendingTimers.Add(new PendingTimer(w.Id, handlerId, "Elapsed", line));
        else if (owner == "System.Web.HttpApplication" && Id(w.Symbol.ContainingType.OriginalDefinition) is { } tid) _pendingAppEvents.Add((tid, ev.Name, handlerId));
        else return false;
        return true;
    }

    /// <summary>Passata 3: new System.Threading.Timer(OnTick, ...).</summary>
    private void OnTimerCreation(BodyWork w, BaseObjectCreationExpressionSyntax oc, IMethodSymbol ctor)
    {
        if (ctor.ContainingType.ToDisplayString() != "System.Threading.Timer" || oc.ArgumentList?.Arguments.FirstOrDefault() is not { } arg) return;
        if (w.Model.HandlerOf(arg.Expression) is { } h && InSource(h) && Id(h.Norm()) is { } hid)
            _pendingTimers.Add(new PendingTimer(w.Id, hid, "callback", oc.Line()));
    }

    // ------------------------------------------------------------------ web.config: moduli e handler registrati

    private sealed record HandlerConfig(HashSet<string> Modules, List<(string? Path, string? Verb, string Type)> Handlers);

    /// <summary>&lt;httpModules&gt;/&lt;modules&gt; e &lt;httpHandlers&gt;/&lt;handlers&gt; di system.web e system.webServer.</summary>
    private HandlerConfig LoadHandlerConfig(Project p)
    {
        var result = new HandlerConfig(new HashSet<string>(), new List<(string?, string?, string)>());
        foreach (var (cfg, _) in ProjectFiles(p, f => Path.GetFileName(f).Equals("web.config", StringComparison.OrdinalIgnoreCase)))
        {
            XElement? root;
            try { root = XDocument.Load(cfg).Root; }
            catch { continue; }   // l'avviso lo da' gia' la lettura delle autorizzazioni
            foreach (var add in root?.Descendants().Where(e => e.Name.LocalName == "add") ?? [])
            {
                var type = ((string?)add.Attribute("type"))?.Split(',')[0].Trim();
                var parent = add.Parent?.Name.LocalName;
                if (string.IsNullOrEmpty(type)) continue;
                if (parent is "httpModules" or "modules") result.Modules.Add(type);
                else if (parent is "httpHandlers" or "handlers") result.Handlers.Add(((string?)add.Attribute("path"), (string?)add.Attribute("verb"), type));
            }
        }
        return result;
    }
}
