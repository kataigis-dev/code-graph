using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;

namespace RoslynGraph;

/// <summary>
/// Passata 5: Web Forms. Pagine (.aspx), controlli utente (.ascx), master page (.master), servizi (.asmx),
/// handler (.ashx), Global.asax e regole di autorizzazione dei web.config.
/// Il markup si legge con espressioni regolari, come le viste Razor: controlli server, eventi, validatori,
/// origini dati, link e redirect. Il code-behind e' gia' nel grafo dalle passate 1 e 3: qui lo si collega alla pagina.
///
///   Route (flavor webforms) -HANDLED_BY-> Page -HANDLES_EVENT-> metodi del code-behind (Page_Load, btnSave_Click, ...)
///   Page -USES_CONTROL-> Page (.ascx), Page -USES_MASTER-> Page (.master), Page -CODE_BEHIND-> Type
///   Page -READS/WRITES/CALLS_PROC-> tabelle (SqlDataSource, EntityDataSource), Page -CALLS-> metodi (ObjectDataSource)
///   asmx: una rotta per ogni [WebMethod]; ashx: rotta -> ProcessRequest; Global.asax: rotta APP -> Application_*, Session_*
/// </summary>
public sealed partial class Extractor
{
    private readonly Dictionary<string, string> _pageIndex = new(StringComparer.OrdinalIgnoreCase);       // "progetto|percorso" -> pagina
    private readonly Dictionary<string, List<string>> _pageByRel = new(StringComparer.OrdinalIgnoreCase); // percorso -> pagine
    private readonly Dictionary<string, string> _pageByType = new();                                       // code-behind -> pagina
    private readonly Dictionary<string, string> _routeByPage = new();                                      // pagina -> rotta
    private readonly Dictionary<string, INamedTypeSymbol> _typesByFqn = new();
    private readonly List<PendingPageRedirect> _pendingPageRedirects = new();
    private readonly List<PendingEvent> _pendingEvents = new();
    private readonly List<(string Project, string Template, string Page)> _pendingPageRoutes = new();

    private sealed record PendingPageRedirect(string FromMethod, string? TypeId, string Project, string Url, int Line);
    private sealed record PendingEvent(string TypeId, string Control, string Event, string Handler);
    private sealed record AuthScope(string Path, List<string> Rules, string Origin);
    private sealed record ParsedPage(SourceFile File, string Text, int[] Lines, Dictionary<string, string> Attrs, INamedTypeSymbol? Type);

    private static readonly HashSet<string> WebFormsExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".aspx", ".ascx", ".master", ".asmx", ".ashx", ".asax" };

    private static readonly HashSet<string> MainDirectives = new(StringComparer.OrdinalIgnoreCase)
        { "Page", "Control", "Master", "WebService", "WebHandler", "Application" };

    // override del ciclo di vita: entry point della pagina anche senza attributi On* nel markup
    private static readonly HashSet<string> LifecycleOverrides = new()
    {
        "OnPreInit", "OnInit", "OnInitComplete", "OnPreLoad", "OnLoad", "OnLoadComplete", "OnPreRender",
        "OnPreRenderComplete", "OnUnload", "CreateChildControls", "Render", "RaisePostBackEvent",
    };

    private void PassWebForms()
    {
        var files = new List<SourceFile>();
        foreach (var (p, _) in _units)
            foreach (var (full, projRel) in ProjectFiles(p, f => WebFormsExtensions.Contains(Path.GetExtension(f))))
                if (AddFileNode(p, full, projRel, "P:", "Page", Path.GetFileName(full)) is { } pf)
                {
                    _g.Get(pf.Id)!["pageKind"] = pf.Kind;
                    _pageIndex[$"{p.Name}|{projRel}"] = pf.Id;
                    _pageByRel.Bucket(projRel).Add(pf.Id);
                    files.Add(pf);
                }
        if (files.Count == 0) return;

        foreach (var (_, ts) in _types) _typesByFqn.TryAdd(ts.ToDisplayString(), ts);
        var auth = new Dictionary<string, List<AuthScope>>();
        foreach (var (p, _) in _units) auth[p.Name] = LoadAuthorization(p);

        // prima code-behind e rotte di tutte le pagine: link e redirect possono puntare a pagine non ancora lette
        var pages = new List<ParsedPage>();
        foreach (var pf in files)
        {
            if (TryRead(pf, "pagina") is not { } text) continue;
            var n = _g.Get(pf.Id)!;
            var (directive, attrs) = MainDirective(text);
            n["directive"] = directive;
            var type = CodeBehindType(attrs, n);
            if (type is not null && Id(type) is { } tid)
            {
                _g.Edge(pf.Id, tid, "CODE_BEHIND");
                _pageByType.TryAdd(tid, pf.Id);
                n["codeBehind"] = type.ToDisplayString();
            }
            AddPageRoutes(pf, type, AuthFor(auth.GetValueOrDefault(pf.Project), pf.ProjRel));
            pages.Add(new ParsedPage(pf, text, LineStarts(text), attrs, type));
        }

        var wired = _pendingEvents.ToLookup(e => e.TypeId);
        foreach (var pp in pages) ParsePageMarkup(pp, wired);
        foreach (var r in _pendingPageRedirects)
        {
            // un URL relativo nel code-behind e' relativo alla cartella della sua pagina
            var pagePath = r.TypeId is not null && _pageByType.TryGetValue(r.TypeId, out var pid) ? (string)_g.Get(pid)!["projectPath"]! : "";
            LinkUrl(r.FromMethod, r.Project, pagePath, r.Url, "REDIRECTS_TO", r.Line, "unresolved_redirects");
        }
        foreach (var (project, template, page) in _pendingPageRoutes)
            if (ResolvePageUrl(project, "", page) is { } pid && _routeByPage.TryGetValue(pid, out var rid))
                _g.Get(rid)!.AddDistinct("friendly_urls", template);
        Info($"   {pages.Count} file Web Forms, {_routeByPage.Count} pagine con rotta");
    }

    // ------------------------------------------------------------------ direttiva, code-behind, rotte

    private static (string? Name, Dictionary<string, string> Attrs) MainDirective(string text)
    {
        var d = WebFormsPatterns.Directive.Matches(text).FirstOrDefault(d => MainDirectives.Contains(d.Groups["d"].Value));
        return d is null ? (null, ParseAttrs("")) : (d.Groups["d"].Value, ParseAttrs(d.Groups["attrs"].Value));
    }

    private INamedTypeSymbol? CodeBehindType(Dictionary<string, string> attrs, GraphNode n)
    {
        var name = attrs.GetValueOrDefault("Inherits") ?? attrs.GetValueOrDefault("Class");
        if (string.IsNullOrWhiteSpace(name)) return null;   // pagina con solo codice inline (<script runat="server">)
        if (FindType(name) is { } t) return t;
        // CodeFile = progetto "web site": il code-behind non e' compilato in un csproj e non e' nel grafo
        n["unresolved_code_behind"] = attrs.ContainsKey("CodeFile") ? $"{name} (CodeFile: progetto web site non supportato)" : name;
        return null;
    }

    /// <summary>Tipo per nome completo ("Ns.Tipo, Assembly"), oppure per nome semplice se e' univoco.</summary>
    private INamedTypeSymbol? FindType(string name)
    {
        name = name.Split(',')[0].Trim();
        if (_typesByFqn.TryGetValue(name, out var t)) return t;
        var candidates = _typesByFqn.Values.Where(x => x.Name == name.Split('.').Last()).Take(2).ToList();
        return candidates.Count == 1 ? candidates[0] : null;
    }

    private void AddPageRoutes(SourceFile pf, INamedTypeSymbol? type, List<string> auth)
    {
        var folder = Paths.FolderOf(pf.ProjRel);
        var group = folder.Length > 0 ? folder : "(radice)";
        var stem = Path.GetFileNameWithoutExtension(pf.ProjRel);
        string Route(string flavor, List<string> verbs, string template, string controller, string action,
                     List<string> routeAuth, List<string> handlers)
        {
            var route = AddRoute(pf.Project, flavor, verbs, template, controller, action, routeAuth, handlers, pf.Id);
            route["page"] = pf.Id;
            return route.Id;
        }

        switch (pf.Kind)
        {
            case "aspx":
                _routeByPage[pf.Id] = Route("webforms", ["GET", "POST"], pf.ProjRel, group, stem, auth, [pf.Id]);
                break;
            case "ashx":
                var process = MethodIds(type, m => m.Name == "ProcessRequest");
                _routeByPage[pf.Id] = Route("ashx", ["ANY"], pf.ProjRel, group, stem, auth, process.Count > 0 ? process : [pf.Id]);
                break;
            case "asmx":
                foreach (var m in MethodsInHierarchy(type).Where(m => m.HasAttr("WebMethod")))
                    if (Id(m.Norm()) is { } mid)
                        Route("asmx", ["POST"], $"{pf.ProjRel}/{m.Name}", stem, m.Name, auth.Concat(m.Authorization()).ToList(), [mid]);
                break;
            case "asax":
                // eventi dell'applicazione, che girano per ogni richiesta (autenticazione, errori, sessione).
                // Global.asax non e' raggiungibile per URL: le regole <authorization> non lo riguardano
                var app = MethodIds(type, m => m.Name.StartsWith("Application_", StringComparison.Ordinal)
                                               || m.Name.StartsWith("Session_", StringComparison.Ordinal)
                                               || (m.IsOverride && m.Name == "Init"));
                if (app.Count > 0) Route("global", ["APP"], pf.ProjRel, "Global", stem, [], app);
                break;
        }
    }

    // ------------------------------------------------------------------ markup

    private void ParsePageMarkup(ParsedPage pp, ILookup<string, PendingEvent> wired)
    {
        var (pf, text, lines) = (pp.File, pp.Text, pp.Lines);
        var n = _g.Get(pf.Id)!;
        n["loc"] = lines.Length;
        var inline = WebFormsPatterns.InlineCode.Matches(text).Count + WebFormsPatterns.ServerScript.Matches(text).Count;
        if (inline > 0) n["inline_code_blocks"] = inline;

        // master page e controlli utente registrati
        if (pp.Attrs.GetValueOrDefault("MasterPageFile") is { Length: > 0 } master) LinkPage(pf, master, "USES_MASTER", null);
        foreach (Match d in WebFormsPatterns.Directive.Matches(text))
            if (d.Groups["d"].Value.Equals("Register", StringComparison.OrdinalIgnoreCase)
                && ParseAttrs(d.Groups["attrs"].Value).GetValueOrDefault("Src") is { Length: > 0 } src)
                LinkPage(pf, src, "USES_CONTROL", LineAt(lines, d.Index));

        if (pp.Type is not null && pf.Kind is "aspx" or "ascx" or "master")
        {
            // AutoEventWireup (Page_Load, Page_Init, ...) e override del ciclo di vita (OnInit, OnLoad, ...)
            foreach (var m in MethodsInHierarchy(pp.Type))
                if (m.Name.StartsWith("Page_", StringComparison.Ordinal)) AddHandler(pf.Id, m, "(pagina)", m.Name[5..], null);
                else if (m.IsOverride && LifecycleOverrides.Contains(m.Name)) AddHandler(pf.Id, m, "(pagina)", m.Name, null);
            // eventi collegati nel code-behind: btnDelete.Click += btnDelete_Click
            foreach (var ev in wired[Id(pp.Type) ?? ""])
                AddEvent(_g.Edge(pf.Id, ev.Handler, "HANDLES_EVENT"), ev.Control, ev.Event);
        }

        foreach (Match m in WebFormsPatterns.Tag.Matches(text)) OnServerTag(pp, n, m);
        foreach (Match u in WebFormsPatterns.PageUrl.Matches(text))
        {
            var posts = u.Groups["attr"].Value.ToLowerInvariant() is "postbackurl" or "action";
            LinkPage(pf, u.Groups["u"].Value, posts ? "POSTS_TO" : "LINKS_TO", LineAt(lines, u.Index));
        }
        foreach (Match r in WebFormsPatterns.InlineRedirect.Matches(text))
            LinkPage(pf, r.Groups["u"].Value, "REDIRECTS_TO", LineAt(lines, r.Index));
        foreach (Match b in WebFormsPatterns.EvalBind.Matches(text))
            n.AddDistinct("bound_fields", $"{b.Groups["f"].Value} ({b.Groups["k"].Value})", 300);
        foreach (Match s in WebFormsPatterns.StateKey.Matches(text))
            n.AddDistinct("state_keys", $"{s.Groups["s"].Value}[{s.Groups["k"].Value}]");
        foreach (Match q in WebFormsPatterns.RequestKey.Matches(text))
            n.AddDistinct("request_keys", $"{(q.Groups["s"].Success ? q.Groups["s"].Value : "Request")}[{q.Groups["k"].Value}]");
        foreach (Match c in WebFormsPatterns.ConnectionString.Matches(text)) n.AddDistinct("connection_strings", c.Groups["n"].Value);
        foreach (Match role in RazorPatterns.Role.Matches(text)) n.AddDistinct("roles_checked", role.Groups["r"].Value);
    }

    private void OnServerTag(ParsedPage pp, GraphNode n, Match m)
    {
        var tagName = m.Groups["name"].Value;
        var attrs = ParseAttrs(m.Groups["attrs"].Value);
        bool prefixed = tagName.IndexOf(':') > 0;   // asp:Button, uc:CustomerCard
        if (!prefixed && !string.Equals(attrs.GetValueOrDefault("runat"), "server", StringComparison.OrdinalIgnoreCase)) return;

        var tag = tagName[(tagName.IndexOf(':') + 1)..];
        var line = LineAt(pp.Lines, m.Index);
        string? Get(string k) => attrs.TryGetValue(k, out var v) && v.Length > 0 ? v : null;
        var controlId = Get("ID");
        if (controlId is not null) n.AddDistinct("controls", $"{controlId} ({tagName})", 300);

        // eventi lato server: OnClick="btnSave_Click"; sui tag HTML con runat="server" solo OnServerClick/OnServerChange
        foreach (var (k, v) in attrs)
        {
            bool serverEvent = k.Length > 2 && k.StartsWith("On", StringComparison.OrdinalIgnoreCase)
                               && !k.StartsWith("OnClient", StringComparison.OrdinalIgnoreCase)
                               && (prefixed || k.StartsWith("OnServer", StringComparison.OrdinalIgnoreCase));
            if (!serverEvent || !WebFormsPatterns.Identifier.IsMatch(v)) continue;
            var handlers = MethodsInHierarchy(pp.Type).Where(x => x.Name == v).ToList();
            foreach (var h in handlers) AddHandler(pp.File.Id, h, controlId ?? tagName, k[2..], line);
            if (handlers.Count == 0) n.AddDistinct("unresolved_handlers", $"{controlId ?? tagName}.{k[2..]} -> {v}");
        }

        switch (tag.ToLowerInvariant())
        {
            case "requiredfieldvalidator" or "rangevalidator" or "regularexpressionvalidator" or "comparevalidator" or "customvalidator":
                n.AddItem("validations", new()
                {
                    ["field"] = Get("ControlToValidate"), ["message"] = Get("ErrorMessage") ?? Get("Text"),
                    ["type"] = tag, ["rule"] = ValidatorRule(tag, Get), ["line"] = line,
                }, 100);
                break;

            case "sqldatasource" or "accessdatasource":
                foreach (var cmd in new[] { "SelectCommand", "InsertCommand", "UpdateCommand", "DeleteCommand" })
                {
                    if (Get(cmd) is not { } sql) continue;
                    if (string.Equals(Get(cmd + "Type"), "StoredProcedure", StringComparison.OrdinalIgnoreCase))
                        SqlEdge(pp.File.Id, "StoredProcedure", sql, "CALLS_PROC", line);
                    else SqlFacts(pp.File.Id, n, sql, line, knownSql: true);
                }
                break;

            case "objectdatasource":
                var typeName = Get("TypeName");
                var ds = typeName is null ? null : FindType(typeName);
                foreach (var attr in new[] { "SelectMethod", "InsertMethod", "UpdateMethod", "DeleteMethod", "SelectCountMethod" })
                {
                    if (Get(attr) is not { } method) continue;
                    var targets = MethodIds(ds, x => x.Name == method);
                    foreach (var mid in targets) _g.Edge(pp.File.Id, mid, "CALLS", line);
                    if (targets.Count == 0) n.AddDistinct("unresolved_calls", $"ObjectDataSource {typeName}.{method}", 50);
                }
                break;

            case "entitydatasource" or "linqdatasource" when (Get("EntitySetName") ?? Get("TableName")) is { } set:
                var tid = DataNode("Table", set, "ef");
                _g.Edge(pp.File.Id, tid, "READS", line)["via"] = "ef";
                bool editable = new[] { "EnableInsert", "EnableUpdate", "EnableDelete" }
                    .Any(k => string.Equals(Get(k), "true", StringComparison.OrdinalIgnoreCase));
                if (editable) _g.Edge(pp.File.Id, tid, "WRITES", line)["via"] = "ef";
                break;

            case "boundfield" or "checkboxfield" or "hyperlinkfield" or "imagefield" when Get("DataField") is { } field:
                n.AddDistinct("bound_fields", $"{field} ({tag})", 300);
                break;

            case "rolegroup":
                var roles = (Get("Roles") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var role in roles) n.AddDistinct("roles_checked", role);
                break;
        }
    }

    private static string? ValidatorRule(string tag, Func<string, string?> get) => tag.ToLowerInvariant() switch
    {
        "rangevalidator" => $"{get("MinimumValue")}..{get("MaximumValue")} ({get("Type") ?? "String"})",
        "regularexpressionvalidator" => get("ValidationExpression"),
        "comparevalidator" => $"{get("Operator") ?? "Equal"} {get("ValueToCompare") ?? get("ControlToCompare")} "
                              + $"({get("Type") ?? "String"})",
        "customvalidator" => get("OnServerValidate") ?? get("ClientValidationFunction"),
        _ => "obbligatorio",
    };

    private void AddHandler(string pageId, IMethodSymbol m, string control, string ev, int? line)
    {
        if (Id(m.Norm()) is { } mid) AddEvent(_g.Edge(pageId, mid, "HANDLES_EVENT", line), control, ev);
    }

    private static void AddEvent(GraphEdge e, string control, string ev)
    {
        e.Props.TryAdd("event", ev);
        e.Props.TryAdd("control", control);
    }

    // ------------------------------------------------------------------ link e redirect

    private void LinkPage(SourceFile from, string url, string edgeType, int? line) =>
        LinkUrl(from.Id, from.Project, from.ProjRel, url, edgeType, line, "unresolved_links");

    /// <summary>Arco verso la rotta della pagina (o la pagina, se non ha rotta); un URL non risolto va in unresolvedKey.</summary>
    private void LinkUrl(string from, string project, string currentPath, string url, string edgeType, int? line, string unresolvedKey)
    {
        if (ResolvePageUrl(project, currentPath, url) is not { } target) _g.Get(from)?.AddDistinct(unresolvedKey, url);
        else _g.Edge(from, _routeByPage.GetValueOrDefault(target) ?? target, edgeType, line);
    }

    /// <summary>"~/X.aspx", "/X.aspx" o un percorso relativo alla pagina corrente; la query string non conta.</summary>
    private string? ResolvePageUrl(string project, string currentPath, string url)
    {
        var u = url.Split('?', '#')[0].Trim().Replace('\\', '/');
        if (u.Length == 0 || u.Contains("://", StringComparison.Ordinal) || u.Contains("<%", StringComparison.Ordinal)) return null;
        var rel = u.StartsWith("~/", StringComparison.Ordinal) ? u[2..]
                : u.StartsWith('/') ? u[1..]
                : Paths.Combine(Paths.FolderOf(currentPath), u);
        if (Paths.Normalize(rel) is not { } path) return null;
        if (_pageIndex.TryGetValue($"{project}|{path}", out var id)) return id;
        // redirect da una libreria verso la pagina di un altro progetto: accettato solo se il percorso e' univoco
        return _pageByRel.TryGetValue(path, out var others) && others.Count == 1 ? others[0] : null;
    }

    private static bool IsPageUrl(string url) => Regex.IsMatch(url, @"\.as[hpm]x\b", RegexOptions.IgnoreCase);

    // ------------------------------------------------------------------ web.config: <authorization>

    /// <summary>
    /// Regole allow/deny di tutti i web.config del progetto: quelle alla radice di un web.config valgono per la sua
    /// cartella, quelle in &lt;location path="..."&gt; per il percorso indicato.
    /// </summary>
    private List<AuthScope> LoadAuthorization(Project p)
    {
        var scopes = new List<AuthScope>();
        foreach (var (cfg, projRel) in ProjectFiles(p, f => Path.GetFileName(f).Equals("web.config", StringComparison.OrdinalIgnoreCase)))
        {
            XElement? root;
            try { root = XDocument.Load(cfg).Root; }
            catch (Exception ex) { Warn($"web.config non leggibile {Rel(cfg)}: {ex.Message}"); continue; }
            var folder = Paths.FolderOf(projRel);
            AddScope(scopes, folder, root, Rel(cfg));
            foreach (var loc in root?.Elements().Where(e => e.Name.LocalName == "location") ?? [])
            {
                var path = ((string?)loc.Attribute("path") ?? "").Trim('/');
                AddScope(scopes, Paths.Combine(folder, path).Trim('/'), loc, $"{Rel(cfg)} location {(path.Length > 0 ? path : "(tutto)")}");
            }
        }
        return scopes;
    }

    /// <summary>Regole di &lt;system.web&gt;&lt;authorization&gt;; nei web.config con namespace XML conta il nome locale.</summary>
    private static void AddScope(List<AuthScope> scopes, string path, XElement? parent, string origin)
    {
        static XElement? Child(XElement? e, string name) => e?.Elements().FirstOrDefault(x => x.Name.LocalName == name);
        var rules = (Child(Child(parent, "system.web"), "authorization")?.Elements() ?? [])
            .Where(e => e.Name.LocalName is "allow" or "deny")
            .Select(e => e.Name.LocalName + string.Concat(e.Attributes().Select(a => $" {a.Name.LocalName}={a.Value}")))
            .ToList();
        if (rules.Count > 0) scopes.Add(new AuthScope(path, rules, origin));
    }

    /// <summary>
    /// Regole che valgono per il file, dalla piu' specifica: ASP.NET le valuta in quest'ordine e si ferma alla prima che corrisponde.
    /// </summary>
    private static List<string> AuthFor(List<AuthScope>? scopes, string projRel) => (scopes ?? [])
        .Where(s => s.Path.Length == 0 || projRel.Equals(s.Path, StringComparison.OrdinalIgnoreCase)
                    || projRel.StartsWith(s.Path + "/", StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(s => s.Path.Length)
        .SelectMany(s => s.Rules.Select(r => $"{r} [{s.Origin}]"))
        .ToList();

    // ------------------------------------------------------------------ helper

    /// <summary>Metodi ordinari del tipo e dei suoi tipi base nel codice della solution (le pagine base del progetto).</summary>
    private IEnumerable<IMethodSymbol> MethodsInHierarchy(INamedTypeSymbol? type)
    {
        for (var t = type; t is not null && InSource(t); t = t.BaseType)
            foreach (var m in t.GetMembers().OfType<IMethodSymbol>())
                if (m.MethodKind == MethodKind.Ordinary) yield return m;
    }

    private List<string> MethodIds(INamedTypeSymbol? type, Func<IMethodSymbol, bool> filter) =>
        MethodsInHierarchy(type).Where(filter).Select(m => Id(m.Norm())).OfType<string>().Distinct().ToList();

    private static Dictionary<string, string> ParseAttrs(string s)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in WebFormsPatterns.Attribute.Matches(s)) d.TryAdd(m.Groups["k"].Value, m.Groups["v"].Value);
        return d;
    }

    private static int[] LineStarts(string text) =>
        text.Select((c, i) => (c, i)).Where(x => x.c == '\n').Select(x => x.i + 1).Prepend(0).ToArray();

    private static int LineAt(int[] starts, int index)
    {
        var i = Array.BinarySearch(starts, index);
        return (i >= 0 ? i : ~i - 1) + 1;
    }
}
