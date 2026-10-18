using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynGraph;

/// <summary>Passata 3: corpi dei metodi. Chiamate, tabelle (EF e SQL), stato implicito, configurazione, validazioni, eccezioni.</summary>
public sealed partial class Extractor
{
    private static readonly (string Prefix, string Category)[] IntegrationMap =
    {
        ("System.Net.Mail.", "email"), ("System.Net.Http.", "http"), ("System.Net.WebClient", "http"),
        ("System.Net.HttpWebRequest", "http"), ("System.Net.WebRequest", "http"), ("RestSharp.", "http"),
        ("System.ServiceModel.", "wcf"), ("System.IO.File", "filesystem"), ("System.IO.Directory", "filesystem"),
        ("System.IO.StreamWriter", "filesystem"), ("System.Messaging.", "msmq"), ("RabbitMQ.", "rabbitmq"),
        ("Microsoft.Azure.ServiceBus", "servicebus"), ("Azure.Messaging.", "azure-messaging"),
        ("System.Data.SqlClient.", "sql"), ("Microsoft.Data.SqlClient.", "sql"), ("Dapper.", "sql"),
        ("System.Data.Entity.", "ef6"), ("Microsoft.EntityFrameworkCore.", "efcore"),
        ("System.DirectoryServices", "ldap"), ("Quartz.", "scheduler"), ("Hangfire.", "scheduler"),
        ("System.Web.HttpContext", "system.web"), ("System.Configuration.ConfigurationManager", "config"),
        ("System.Runtime.Caching", "cache"), ("StackExchange.Redis", "cache"),
    };

    private static readonly string[] IgnoredExternalPrefixes =
    {
        "System.Linq.", "System.Collections.", "System.String.", "System.Object.", "System.Text.StringBuilder.",
        "System.Threading.Tasks.Task", "System.Int32.", "System.Int64.", "System.Decimal.", "System.Double.",
        "System.Boolean.", "System.Math.", "System.Nullable", "System.Guid.", "System.Enum.",
    };

    // nomi CLR senza parametri generici: "System.String", non "string" come darebbe FullyQualifiedFormat (UseSpecialTypes),
    // altrimenti i prefissi qui sopra non corrisponderebbero mai
    private static readonly SymbolDisplayFormat ExternalTypeFormat = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.None);

    private void PassBody(BodyWork w)
    {
        var n = _g.Get(w.Id)!;
        var ctrl = _controllers.GetValueOrDefault(Id(w.Symbol.ContainingType.OriginalDefinition) ?? "");
        bool procContext = w.Body.ToString().Contains("StoredProcedure", StringComparison.Ordinal);
        int complexity = 1;
        foreach (var node in w.Body.DescendantNodesAndSelf())
        {
            switch (node)
            {
                case InvocationExpressionSyntax inv: OnInvocation(w, n, inv, ctrl); break;
                case BaseObjectCreationExpressionSyntax oc: OnCreation(w, n, oc); break;
                case IdentifierNameSyntax idn: OnIdentifier(w, n, idn); break;
                case LiteralExpressionSyntax lit when lit.IsKind(SyntaxKind.StringLiteralExpression):
                    OnSqlText(w, n, lit.Token.ValueText, lit, procContext);
                    break;
                case InterpolatedStringExpressionSyntax istr:
                    // i "buchi" dell'interpolazione diventano @p: non vengono scambiati per nomi di tabella
                    var holes = string.Concat(istr.Contents.Select(c => (c as InterpolatedStringTextSyntax)?.TextToken.ValueText ?? " @p "));
                    OnSqlText(w, n, holes, null, false);
                    break;
                case ThrowStatementSyntax { Expression: { } te }: OnThrow(w, n, te); break;
                case ThrowExpressionSyntax tx: OnThrow(w, n, tx.Expression); break;
                case ElementAccessExpressionSyntax ea: OnElementAccess(w, n, ea); break;
                case AssignmentExpressionSyntax asg: OnAssignment(w, n, asg); break;
                case MemberAccessExpressionSyntax ma: OnMemberAccess(n, ma); break;
            }
            if (IsBranch(node)) complexity++;
        }
        n["complexity"] = complexity;
    }

    /// <summary>Punti di decisione della complessita' ciclomatica.</summary>
    private static bool IsBranch(SyntaxNode node) =>
        node is IfStatementSyntax or ConditionalExpressionSyntax or CaseSwitchLabelSyntax or CasePatternSwitchLabelSyntax
            or SwitchExpressionArmSyntax or ForStatementSyntax or ForEachStatementSyntax or WhileStatementSyntax
            or DoStatementSyntax or CatchClauseSyntax
        || node.IsKind(SyntaxKind.LogicalAndExpression) || node.IsKind(SyntaxKind.LogicalOrExpression)
        || node.IsKind(SyntaxKind.CoalesceExpression);

    private void OnInvocation(BodyWork w, GraphNode n, InvocationExpressionSyntax inv, Controller? ctrl)
    {
        var target = w.Model.SymbolOf(inv) as IMethodSymbol;
        var name = target?.Name ?? inv.InvokedName();
        var line = inv.Line();

        // MVC: viste e redirect, risolti nella passata 4
        if (ctrl is not null && name is "View" or "PartialView" && (target is null || !InSource(target)))
            _pendingViews.Add(new PendingView(w.Id, ctrl, inv.LiteralArg(0) ?? _actions.GetValueOrDefault(w.Id) ?? w.Symbol.Name,
                                              name == "PartialView", line));
        else if (ctrl is not null && name == "RedirectToAction" && inv.LiteralArg(0) is { } action)
            _pendingRedirects.Add(new PendingRedirect(w.Id, inv.LiteralArg(1) ?? ctrl.Name, action, line));
        else if (ctrl is not null && name == "Json")
            n["returns_json"] = true;

        // navigazione verso pagine Web Forms (passata 5): Response.Redirect("~/X.aspx?id=" + id), Server.Transfer, Redirect(...)
        if (name is "Redirect" or "RedirectPermanent" or "Transfer" or "Execute"
            && inv.ArgumentList.Arguments.FirstOrDefault()?.Expression.LeadingLiteral() is { } url && IsPageUrl(url))
            _pendingPageRedirects.Add(new PendingPageRedirect(w.Id, Id(w.Symbol.ContainingType.OriginalDefinition), w.Project, url, line));

        // URL "amichevoli" verso pagine: RouteTable.Routes.MapPageRoute("nome", "clienti/{id}", "~/Customers/Edit.aspx")
        if (name == "MapPageRoute" && inv.LiteralArg(1) is { } friendly && inv.LiteralArg(2) is { } page)
            _pendingPageRoutes.Add((w.Project, friendly, page));

        if (name == "AddModelError")
            n.AddItem("validations", new() { ["field"] = inv.LiteralArg(0), ["message"] = inv.LiteralArg(1), ["line"] = line }, 50);

        // EF: context.Set<T>(); con T parametro di tipo (repository generico) la tabella non si puo' sapere
        if (name == "Set" && target is { IsGenericMethod: true } && target.ContainingType.IsDbContext())
        {
            if (target.TypeArguments[0] is ITypeParameterSymbol)
                n.AddDistinct("unresolved_calls",
                              Text.Truncate(inv.ToString(), 100) + " (entita' generica: tabella non determinabile)", 50);
            else TableAccess(w, target.TypeArguments[0], null, inv.IsDbSetWrite() ? "WRITES" : "READS", line);
        }
        if (name is "SaveChanges" or "SaveChangesAsync") n["saves_changes"] = true;

        if (target is null) n.AddDistinct("unresolved_calls", Text.Truncate(inv.Expression.ToString(), 120), 50);
        else if (InSource(target.Norm())) Link(w.Id, Id(target.Norm()), "CALLS", line);
        else RecordExternal(n, target.Norm());
    }

    private void OnCreation(BodyWork w, GraphNode n, BaseObjectCreationExpressionSyntax oc)
    {
        if (w.Model.SymbolOf(oc) is not IMethodSymbol ctor) return;
        var c = ctor.Norm();
        if (!InSource(c)) { RecordExternal(n, c); return; }
        Link(w.Id, Id(c), "CALLS", oc.Line());
        Link(w.Id, Id(c.ContainingType.OriginalDefinition), "INSTANTIATES", oc.Line());
    }

    private void OnIdentifier(BodyWork w, GraphNode n, IdentifierNameSyntax idn)
    {
        var text = idn.Identifier.ValueText;
        var si = w.Model.GetSymbolInfo(idn);
        if (text is "Session" or "TempData" or "ViewBag" or "ViewData" or "ViewState" or "Application" or "Cache"
            && (si.Symbol ?? si.CandidateSymbols.FirstOrDefault()).IsWebState())
            n.AddDistinct("state_usage", text);

        if (si.Symbol is not IPropertySymbol prop) return;
        if (prop.Type.DbSetEntity() is { } entity)
            TableAccess(w, entity, prop.Name, idn.IsDbSetWrite() ? "WRITES" : "READS", idn.Line());
        // proprieta' con logica nel getter o nel setter: diventano chiamate (gli archi verso le auto-property vengono potati)
        else if (InSource(prop) && (idn.IsAssignmentTarget() ? prop.SetMethod : prop.GetMethod) is { } accessor)
            Link(w.Id, Id(accessor.Norm()), "CALLS", idn.Line());
    }

    private void OnSqlText(BodyWork w, GraphNode n, string text, LiteralExpressionSyntax? lit, bool procContext)
    {
        if (text.Length < 4 || SqlFacts(w.Id, n, text, lit?.Line())) return;
        // new SqlCommand("usp_Nome", conn) { CommandType = StoredProcedure } / Dapper con commandType
        if (procContext && lit is not null && SqlHeuristics.LooksLikeProcName(text) && lit.IsCommandText())
            SqlEdge(w.Id, "StoredProcedure", text, "CALLS_PROC", lit.Line());
    }

    /// <summary>Tabelle e procedure di un testo SQL; false se il testo non sembra SQL (con knownSql si registra comunque).</summary>
    private bool SqlFacts(string fromId, GraphNode n, string text, int? line, bool knownSql = false)
    {
        var r = SqlHeuristics.Analyze(text);
        if (!r.IsSql && !knownSql) return false;
        n.AddDistinct("sql_snippets", Text.OneLine(text, 300), 20);
        foreach (var t in r.Reads) SqlEdge(fromId, "Table", t, "READS", line);
        foreach (var t in r.Writes) SqlEdge(fromId, "Table", t, "WRITES", line);
        foreach (var t in r.Procs) SqlEdge(fromId, "StoredProcedure", t, "CALLS_PROC", line);
        return true;
    }

    private void SqlEdge(string fromId, string kind, string rawName, string edgeType, int? line)
    {
        var e = _g.Edge(fromId, DataNode(kind, rawName, "sql", rawName.Replace("[", "").Replace("]", "")), edgeType, line);
        e["via"] = "sql";
        e["confidence"] = "heuristic";
    }

    private void TableAccess(BodyWork w, ITypeSymbol entity, string? fallbackName, string edgeType, int line, string via = "ef")
    {
        var tid = Id(entity.OriginalDefinition) is { } eid && _entityToTable.TryGetValue(eid, out var known)
            ? known : DataNode("Table", entity.TableName(fallbackName), "ef");
        var e = _g.Edge(w.Id, tid, edgeType, line);
        // a parita' di arco vince l'evidenza piu' forte: "ef" (DbSet, Add, Remove) su "ef-tracking" (euristica)
        if (via == "ef" || e["via"] is null) e["via"] = via;
        e["confidence"] = e["via"] is "ef-tracking" ? "heuristic" : null;
    }

    /// <summary>
    /// Eventi collegati nel codice (btnSave.Click += btnSave_Click) e update via change tracking di EF, il caso piu' comune
    /// in EF6: entita.Prop = valore su un'entita' mappata, oppure context.Entry(x).State = ... . Questi diventano archi WRITES
    /// euristici (via = "ef-tracking"): sono scritture solo se poi qualcuno chiama SaveChanges, cosa che qui non si verifica.
    /// </summary>
    private void OnAssignment(BodyWork w, GraphNode n, AssignmentExpressionSyntax asg)
    {
        if (asg.IsKind(SyntaxKind.AddAssignmentExpression) && w.Model.GetSymbolInfo(asg.Left).Symbol is IEventSymbol ev)
        {
            if (w.Model.HandlerOf(asg.Right) is { } h && InSource(h) && Id(h.Norm()) is { } hid
                && Id(w.Symbol.ContainingType.OriginalDefinition) is { } owner)
            {
                var control = asg.Left is MemberAccessExpressionSyntax evm ? evm.Expression.ToString() : "this";
                _pendingEvents.Add(new PendingEvent(owner, control, ev.Name, hid));
            }
            return;
        }

        if (asg.Left is not MemberAccessExpressionSyntax { Expression: not (ThisExpressionSyntax or BaseExpressionSyntax) } left
            || w.Model.GetTypeInfo(left.Expression).Type is not { } targetType) return;
        if (left.Name.Identifier.ValueText == "State" && targetType is INamedTypeSymbol { Name: "DbEntityEntry" or "EntityEntry" } entry)
        {
            // Entry<T>(x) generico, oppure Entry(x) non generico: l'entita' e' il tipo dell'argomento
            var entity = entry.TypeArguments.FirstOrDefault();
            if (entity is null && left.Expression is InvocationExpressionSyntax { ArgumentList.Arguments: [var arg, ..] })
                entity = w.Model.GetTypeInfo(arg.Expression).Type;
            if (entity is not null and not ITypeParameterSymbol) TableAccess(w, entity, null, "WRITES", asg.Line(), "ef-tracking");
        }
        else if (Id(targetType.OriginalDefinition) is { } eid && _entityToTable.ContainsKey(eid))
            TableAccess(w, targetType, null, "WRITES", asg.Line(), "ef-tracking");
    }

    private static void OnThrow(BodyWork w, GraphNode n, ExpressionSyntax expr)
    {
        if (expr is not BaseObjectCreationExpressionSyntax oc) return;
        var message = oc.ArgumentList?.Arguments.FirstOrDefault()?.Expression switch
        {
            null => null,
            LiteralExpressionSyntax l => l.Token.ValueText,
            var a => Text.Truncate(a.ToString(), 160),
        };
        var type = w.Model.GetTypeInfo(oc).Type?.Name ?? (oc as ObjectCreationExpressionSyntax)?.Type.ToString() ?? "?";
        n.AddItem("throws", new() { ["type"] = type, ["message"] = message, ["line"] = oc.Line() }, 30);
    }

    private static void OnElementAccess(BodyWork w, GraphNode n, ElementAccessExpressionSyntax ea)
    {
        if (ea.ArgumentList.Arguments.FirstOrDefault()?.Expression is not LiteralExpressionSyntax { Token.ValueText: var key }) return;
        var last = ea.Expression.ToString().Split('.').Last();
        switch (last)
        {
            case "AppSettings": n.AddDistinct("config_keys", key); break;
            case "ConnectionStrings": n.AddDistinct("connection_strings", key); break;
            case "Session" or "TempData" or "ViewData" or "ViewState" or "Application" or "Cache"
                 or "Form" or "QueryString" or "Request" or "Params" when w.Model.SymbolOf(ea.Expression).IsWebState():
                n.AddDistinct(last is "Form" or "QueryString" or "Request" or "Params" ? "request_keys" : "state_keys", $"{last}[{key}]");
                break;
        }
    }

    private static void OnMemberAccess(GraphNode n, MemberAccessExpressionSyntax ma)
    {
        if (ma.Expression is IdentifierNameSyntax { Identifier.ValueText: "ViewBag" })
            n.AddDistinct("viewbag_keys", ma.Name.Identifier.ValueText);
        else if (ma.Name.Identifier.ValueText == "Current" && ma.Expression.ToString().EndsWith("HttpContext", StringComparison.Ordinal))
            n.AddDistinct("state_usage", "HttpContext.Current");
    }

    private static void RecordExternal(GraphNode n, IMethodSymbol t)
    {
        if (t.ContainingType is not { } type) return;
        var key = $"{type.OriginalDefinition.ToDisplayString(ExternalTypeFormat)}.{t.Name}";
        if (IgnoredExternalPrefixes.Any(p => key.StartsWith(p, StringComparison.Ordinal))) return;
        n.AddDistinct("external_calls", key, 100);
        foreach (var (prefix, category) in IntegrationMap)
            if (key.StartsWith(prefix, StringComparison.Ordinal)) n.AddDistinct("integrations", category);
        n.Flag("uses_system_web", key.StartsWith("System.Web.", StringComparison.Ordinal));
    }
}
