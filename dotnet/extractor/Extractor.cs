using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynGraph;

/// <summary>
/// Costruisce il grafo in cinque passate:
///  0. schema SQL (Extractor.Sql.cs): tabelle, colonne, vincoli, viste, stored procedure, funzioni, trigger
///  1. dichiarazioni (qui): progetti, tipi, metodi, ereditarieta', dispatch interfaccia->implementazione, DbContext/DbSet
///  2. MVC (qui): controller, action, rotte, verbi, autorizzazioni
///  3. corpi dei metodi (Extractor.Code.cs): chiamate, tabelle (EF e SQL), stato implicito, config, validazioni, eccezioni
///  4. viste Razor (Extractor.Razor.cs): modello, campi legati, link e form verso le rotte, partial, layout
///  5. Web Forms (Extractor.WebForms.cs): pagine, controlli, master, servizi asmx e svc, handler ashx, Global.asax, web.config
///  6. altri entry point (Extractor.EntryPoints.cs): WCF, Windows Service, batch, job, timer, messaggi, moduli e handler HTTP, hub
///  7. configurazione (Extractor.Config.cs): valori delle chiavi appSettings per file e ambiente
/// </summary>
public sealed partial class Extractor(CodeGraph graph, string root, Options opt)
{
    private readonly CodeGraph _g = graph;
    private readonly string _root = Path.GetFullPath(root);
    private readonly HashSet<string> _ownAssemblies = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _docIdOwner = new();      // doc id -> assembly che lo dichiara per primo
    private readonly List<(Project Project, Compilation Compilation)> _units = new();
    private readonly List<(Project Project, INamedTypeSymbol Type)> _types = new();
    private readonly List<BodyWork> _bodies = new();
    private readonly Dictionary<string, string> _entityToTable = new();    // type id dell'entita' -> table id
    private readonly Dictionary<string, Controller> _controllers = new();   // type id -> controller
    private readonly Dictionary<string, string> _actions = new();           // method id -> nome dell'action
    private readonly Dictionary<string, List<string>> _routesByAction = new(StringComparer.OrdinalIgnoreCase); // "controller/action"
    private readonly List<string> _log = new();

    private sealed record BodyWork(string Id, IMethodSymbol Symbol, SyntaxNode Body, SemanticModel Model, string Project);
    private sealed record Controller(string Project, string Name, string? Area);
    private sealed record SourceFile(string Id, string Project, string ProjRel, string FullPath)
    {
        public string Kind => Path.GetExtension(ProjRel)[1..].ToLowerInvariant();
    }

    public async Task RunAsync(Solution solution)
    {
        var projects = solution.Projects
            .Where(p => p.Language == LanguageNames.CSharp && (opt.IncludeTests || !IsTestProject(p.Name)))
            .DistinctBy(p => p.FilePath ?? p.Name)   // multi-target: un solo TFM
            .ToList();
        _ownAssemblies.UnionWith(projects.Select(p => p.AssemblyName));

        var projectMeta = new List<object>();
        foreach (var p in projects)
        {
            Info($"compilazione {p.Name} ...");
            if (await p.GetCompilationAsync() is not { } comp) { Warn($"{p.Name}: compilazione non disponibile, saltato"); continue; }
            var errors = comp.GetDeclarationDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            if (errors.Count > 0)
                Warn($"{p.Name}: {errors.Count} errori di dichiarazione (riferimenti mancanti? hai fatto il restore NuGet?)");
            foreach (var e in errors.Take(3)) Warn("   " + e.GetMessage());

            _units.Add((p, comp));
            var n = _g.Node("PRJ:" + p.Name, "Project", p.Name);
            n["file"] = Rel(p.FilePath);
            n["assembly"] = p.AssemblyName;
            n["declarationErrors"] = errors.Count;
            n["references"] = p.MetadataReferences.Count;
            foreach (var target in p.ProjectReferences.Select(r => solution.GetProject(r.ProjectId)).OfType<Project>())
                _g.Edge(n.Id, "PRJ:" + target.Name, "REFERENCES_PROJECT");
            projectMeta.Add(new { name = p.Name, declarationErrors = errors.Count, references = p.MetadataReferences.Count });
        }
        _g.Meta["projects"] = projectMeta;

        Info("passata 0/7: schema SQL");
        PassSql();
        Info("passata 1/7: dichiarazioni");
        foreach (var (p, comp) in _units) PassDeclarations(p, comp);
        Info("passata 2/7: controller e rotte");
        PassMvc();
        Info($"passata 3/7: corpi di {_bodies.Count} metodi");
        _bodies.ForEach(PassBody);
        Info("passata 4/7: viste Razor");
        PassRazor();
        Info("passata 5/7: Web Forms e web.config");
        PassWebForms();
        Info("passata 6/7: entry point non HTTP");
        PassEntryPoints();
        Info("passata 7/7: configurazione");
        PassConfig();

        _g.Meta["prunedDanglingEdges"] = _g.PruneDanglingEdges();
        _g.Meta["log"] = _log.Take(500).ToList();
    }

    // ------------------------------------------------------------------ passata 1: dichiarazioni

    private void PassDeclarations(Project p, Compilation comp)
    {
        var dir = Path.GetDirectoryName(p.FilePath);
        foreach (var tree in comp.SyntaxTrees.Where(t => !Paths.IsExcluded(t.FilePath, dir)))
        {
            var model = comp.GetSemanticModel(tree);
            bool generated = Paths.IsGenerated(tree.FilePath, dir);
            foreach (var node in tree.GetRoot().DescendantNodes())
            {
                switch (node)
                {
                    case BaseTypeDeclarationSyntax td when model.GetDeclaredSymbol(td) is INamedTypeSymbol ts:
                        AddType(p, ts, td, generated);
                        break;
                    case BaseMethodDeclarationSyntax md when model.GetDeclaredSymbol(md) is IMethodSymbol ms:
                        AddMethod(p, ms, md, md.Body(), model, generated);
                        break;
                    // gli accessor automatici non hanno logica: nodo solo se c'e' un corpo o se e' astratto o di interfaccia
                    case AccessorDeclarationSyntax ad when model.GetDeclaredSymbol(ad) is IMethodSymbol acc:
                        if (ad.Body() is not null || acc.IsAbstract || acc.ContainingType.TypeKind == TypeKind.Interface)
                            AddMethod(p, acc, ad, ad.Body(), model, generated);
                        break;
                    case PropertyDeclarationSyntax { ExpressionBody: { } body } pd
                        when model.GetDeclaredSymbol(pd) is IPropertySymbol { GetMethod: { } getter }:
                        AddMethod(p, getter, pd, body, model, generated);
                        break;
                }
            }
        }
    }

    private void AddType(Project p, INamedTypeSymbol ts, BaseTypeDeclarationSyntax td, bool generated)
    {
        if (Id(ts) is not { } id) return;
        bool otherPart = _g.Has(id);
        var n = _g.Node(id, "Type", ts.Name);
        n.AddDistinct("files", Rel(td.SyntaxTree.FilePath), 20);
        if (otherPart)
        {
            // classe partial: la parte scritta a mano vince su quella generata (Edit.aspx.designer.cs, Form1.Designer.cs)
            if (!generated && n.Props.Remove("generated")) SetLocation(n, td);
            return;
        }

        _types.Add((p, ts));
        n["fqn"] = ts.ToDisplayString();
        n["typeKind"] = ts.TypeKind.ToString();
        n["namespace"] = ts.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() : null;
        n["project"] = p.Name;
        n["accessibility"] = ts.DeclaredAccessibility.ToString();
        SetLocation(n, td);
        n.Flag("abstract", ts.IsAbstract && ts.TypeKind == TypeKind.Class);
        n.Flag("static", ts.IsStatic);
        n.Flag("generated", generated);
        AddAttributes(n, ts);
        AddDoc(n, ts);
        AddProperties(n, ts);
        if (ts.TypeKind == TypeKind.Class) RegisterMediatrHandlers(ts);

        _g.Edge("PRJ:" + p.Name, id, "CONTAINS");
        Link(Id(ts.ContainingType?.OriginalDefinition), id, "CONTAINS");
        if (ts.BaseType is { SpecialType: not (SpecialType.System_Object or SpecialType.System_ValueType) } bt)
        {
            n["baseType"] = bt.ToDisplayString();
            Link(id, Id(bt.OriginalDefinition), "INHERITS");
        }
        foreach (var i in ts.Interfaces)
        {
            n.AddDistinct("interfaces", i.ToDisplayString());
            Link(id, Id(i.OriginalDefinition), "IMPLEMENTS");
        }

        // dispatch interfaccia -> implementazione: senza questi archi il call graph si ferma alle interfacce iniettate via DI
        if (ts.TypeKind is TypeKind.Class or TypeKind.Struct)
            foreach (var member in ts.AllInterfaces.SelectMany(i => i.GetMembers().OfType<IMethodSymbol>()))
                if (ts.FindImplementationForInterfaceMember(member) is IMethodSymbol impl
                    && SymbolEqualityComparer.Default.Equals(impl.ContainingType.OriginalDefinition, ts.OriginalDefinition))
                    Link(Id(member.Norm()), Id(impl.Norm()), "DISPATCHES_TO");

        // EF6 / EF Core: i DbSet<T> del contesto diventano tabelle
        if (!ts.IsDbContext()) return;
        n["isDbContext"] = true;
        foreach (var prop in ts.GetMembers().OfType<IPropertySymbol>())
        {
            if (prop.Type.DbSetEntity() is not { } entity) continue;
            var tid = DataNode("Table", entity.TableName(prop.Name), "ef");
            _g.Get(tid)!["entity"] = entity.ToDisplayString();
            if (Id(entity.OriginalDefinition) is { } eid)
            {
                _entityToTable[eid] = tid;
                _g.Edge(tid, eid, "MAPPED_TO");
            }
            _g.Edge(id, tid, "DECLARES_TABLE");
        }
    }

    private void AddMethod(Project p, IMethodSymbol symbol, SyntaxNode decl, SyntaxNode? body, SemanticModel model, bool generated)
    {
        var m = symbol.Norm();
        if (Id(m) is not { } id) return;
        var n = _g.Node(id, "Method", m.DisplayName());
        // metodo partial: dichiarazione e implementazione; vince quella con il corpo
        if (n.Props.ContainsKey("signature") && (body is null || n.Props.ContainsKey("hasBody"))) return;

        n["signature"] = m.ToDisplayString();
        n["methodKind"] = m.MethodKind.ToString();
        n["project"] = p.Name;
        n["containingType"] = m.ContainingType.ToDisplayString();
        n["accessibility"] = m.DeclaredAccessibility.ToString();
        var (start, end) = SetLocation(n, decl);
        n["loc"] = end - start + 1;
        n["returnType"] = m.ReturnType.ToDisplayString();
        n["parameters"] = m.Parameters.Select(x => $"{x.Type.ToDisplayString()} {x.Name}").ToList();
        n.Flag("static", m.IsStatic);
        n.Flag("async", m.IsAsync);
        n.Flag("virtual", m.IsVirtual);
        n.Flag("override", m.IsOverride);
        n["abstract"] = body is null ? true : null;   // l'implementazione di un metodo partial toglie l'abstract della dichiarazione
        n.Flag("hasBody", body is not null);
        n.Flag("generated", generated);
        AddAttributes(n, m);
        AddDoc(n, m);

        Link(Id(m.ContainingType.OriginalDefinition), id, "CONTAINS");
        if (m.OverriddenMethod is { } ov) Link(Id(ov.Norm()), id, "DISPATCHES_TO");
        foreach (var t in m.Parameters.Select(x => x.Type).Prepend(m.ReturnType).SelectMany(t => t.Flatten()))
            if (InSource(t)) Link(id, Id(t.OriginalDefinition), "USES_TYPE");
        if (body is not null) _bodies.Add(new BodyWork(id, m, body, model, p.Name));
    }

    // ------------------------------------------------------------------ passata 2: MVC

    private void PassMvc()
    {
        foreach (var (p, ts) in _types)
        {
            if (ts.ControllerFlavor() is not { } flavor) continue;
            var cid = Id(ts)!;
            var cn = _g.Get(cid)!;
            cn["isController"] = true;
            cn["controllerFlavor"] = flavor;
            var name = ts.Name.EndsWith("Controller", StringComparison.Ordinal) ? ts.Name[..^"Controller".Length] : ts.Name;
            var area = ts.Area();
            _controllers[cid] = new Controller(p.Name, name, area);
            var prefix = ts.AttrString("RoutePrefix") ?? (flavor == "aspnetcore" ? ts.AttrString("Route") : null);
            var classAuth = ts.Authorization();

            foreach (var m in ts.GetMembers().OfType<IMethodSymbol>())
            {
                if (m is not { MethodKind: MethodKind.Ordinary, DeclaredAccessibility: Accessibility.Public }
                    || m.IsStatic || m.IsAbstract || m.HasAttr("NonAction") || !InSource(m)) continue;

                var action = m.AttrString("ActionName") ?? m.Name;
                var template = (m.AttrString("Route"), flavor) switch
                {
                    ({ } r, _) when r.StartsWith("~/", StringComparison.Ordinal) => r[2..],
                    ({ } r, _) => Paths.JoinUrl(prefix, r),
                    (_, "aspnetcore") when prefix is not null => prefix,
                    (_, "webapi") => Paths.JoinUrl(prefix ?? "api/" + name, ""),
                    _ => (area is null ? "" : area + "/") + $"{name}/{action}",
                };
                template = template.Replace("[controller]", name).Replace("[action]", action).Trim('/');

                var mid = Id(m.Norm())!;
                var route = AddRoute(p.Name, flavor, m.Verbs(flavor), template, name, action,
                                     classAuth.Concat(m.Authorization()).Distinct().ToList(), [mid], cid);
                route["area"] = area;
                var filters = m.EffectiveAttributes().Concat(ts.EffectiveAttributes()).Select(x => x.Attr.AttrName())
                    .Where(a => a.Length > 0 && !a.StartsWith("Http", StringComparison.Ordinal)
                                && a is not ("Route" or "RoutePrefix" or "ActionName" or "Authorize" or "AllowAnonymous"))
                    .Distinct().ToList();
                if (filters.Count > 0) route["filters"] = filters;
                _actions[mid] = action;
                _routesByAction.Bucket($"{name}/{action}").Add(route.Id);
            }
        }
        Info($"   {_actions.Count} action in {_controllers.Count} controller");
    }

    // ------------------------------------------------------------------ helper comuni alle passate

    /// <summary>
    /// Id del nodo: l'id di documentazione Roslyn. Se lo stesso id e' dichiarato in due progetti della solution
    /// (classi copiate con lo stesso namespace), il secondo riceve il suffisso "@Assembly": altrimenti i due nodi
    /// si fonderebbero e i chiamanti di un progetto vedrebbero il codice dell'altro.
    /// </summary>
    private string? Id(ISymbol? s)
    {
        var id = s?.GetDocumentationCommentId();
        if (id is null || s!.ContainingAssembly is not { } asm || !_ownAssemblies.Contains(asm.Name)) return id;
        _docIdOwner.TryAdd(id, asm.Name);
        return _docIdOwner[id] == asm.Name ? id : $"{id}@{asm.Name}";
    }

    private bool InSource(ISymbol s) =>
        s.Locations.Any(l => l.IsInSource) && s.ContainingAssembly is { } a && _ownAssemblies.Contains(a.Name);

    /// <summary>Arco tra simboli che potrebbero non avere un nodo (esterni o senza id di documentazione).</summary>
    private void Link(string? from, string? to, string type, int? line = null)
    {
        if (from is not null && to is not null) _g.Edge(from, to, type, line);
    }

    /// <summary>url = false per gli entry point senza URL (job, servizi, messaggi): l'id e' "VERBO nome" invece di "VERBO /percorso".</summary>
    private GraphNode AddRoute(string project, string flavor, List<string> verbs, string template, string controller, string action,
                               List<string> auth, IEnumerable<string> handlers, string exposedBy, bool url = true)
    {
        var label = $"{string.Join("|", verbs)} {(url ? "/" : "")}{template}";
        var id = "R:" + label;
        for (int i = 2; _g.Has(id); i++) id = $"R:{label} #{i}";
        var n = _g.Node(id, "Route", label);
        n["controller"] = controller;
        n["action"] = action;
        n["verbs"] = verbs;
        n["template"] = template;
        n["flavor"] = flavor;
        n["project"] = project;
        if (auth.Count > 0) n["authorization"] = auth;
        foreach (var h in handlers) _g.Edge(id, h, "HANDLED_BY");
        _g.Edge(exposedBy, id, "EXPOSES");
        return n;
    }

    /// <summary>Tabella o stored procedure; l'id normalizza parentesi, maiuscole e schema dbo.</summary>
    private string DataNode(string kind, string name, string source, string? display = null)
    {
        var id = (kind == "Table" ? "DB:" : "SP:") + SqlHeuristics.NormalizeName(name);
        _g.Node(id, kind, display ?? name).Props.TryAdd("source", source);
        return id;
    }

    /// <summary>Nodo di un file di vista o Web Forms; null se il file e' gia' nel grafo.</summary>
    private SourceFile? AddFileNode(Project p, string full, string projRel, string prefix, string kind, string name)
    {
        var id = prefix + Rel(full);
        if (_g.Has(id)) return null;
        var n = _g.Node(id, kind, name);
        n["file"] = Rel(full);
        n["project"] = p.Name;
        n["projectPath"] = projRel;
        _g.Edge("PRJ:" + p.Name, id, "CONTAINS");
        return new SourceFile(id, p.Name, projRel, full);
    }

    /// <summary>File del progetto in ordine stabile, bin e obj esclusi, con il percorso relativo alla cartella del progetto.</summary>
    private static IEnumerable<(string Full, string ProjRel)> ProjectFiles(Project p, Func<string, bool> match)
    {
        if (Path.GetDirectoryName(p.FilePath) is not { } dir || !Directory.Exists(dir)) yield break;
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Where(match).Order(StringComparer.Ordinal))
            if (!Paths.IsExcluded(f, dir)) yield return (f, Path.GetRelativePath(dir, f).Replace('\\', '/'));
    }

    private string? TryRead(SourceFile f, string what)
    {
        try { return File.ReadAllText(f.FullPath); }
        catch (Exception ex) { Warn($"{what} non leggibile {f.ProjRel}: {ex.Message}"); return null; }
    }

    private (int Start, int End) SetLocation(GraphNode n, SyntaxNode node)
    {
        var span = node.GetLocation().GetLineSpan();
        n["file"] = Rel(span.Path);
        n["startLine"] = span.StartLinePosition.Line + 1;
        n["endLine"] = span.EndLinePosition.Line + 1;
        return (span.StartLinePosition.Line + 1, span.EndLinePosition.Line + 1);
    }

    private string Rel(string? path) => string.IsNullOrEmpty(path) ? "" : Path.GetRelativePath(_root, path).Replace('\\', '/');

    private static void AddAttributes(GraphNode n, ISymbol s)
    {
        foreach (var name in s.GetAttributes().Select(a => a.AttrName()))
            if (name is not ("" or "CompilerGenerated" or "DebuggerStepThrough" or "GeneratedCode")) n.AddDistinct("attributes", name, 30);
    }

    /// <summary>
    /// Proprieta' pubbliche con i loro attributi, come scritti nel codice: [Required], [StringLength(100)], [Range(0, 50000)],
    /// [Column("...")]. Servono al dizionario dati e alle validazioni di DTO, ViewModel ed entita'.
    /// </summary>
    private void AddProperties(GraphNode n, INamedTypeSymbol ts)
    {
        if (ts.TypeKind is not (TypeKind.Class or TypeKind.Struct)) return;
        foreach (var prop in ts.GetMembers().OfType<IPropertySymbol>())
        {
            if (prop.IsStatic || prop.IsIndexer || prop.DeclaredAccessibility != Accessibility.Public) continue;
            var attrs = prop.GetAttributes().Select(a => a.ApplicationSyntaxReference?.GetSyntax().ToString() ?? a.AttrName())
                .Where(a => a.Length > 0).Take(10).ToList();
            var span = prop.Locations.FirstOrDefault(l => l.IsInSource)?.GetLineSpan();
            n.AddItem("properties", new()
            {
                ["name"] = prop.Name, ["type"] = prop.Type.ToDisplayString(), ["attributes"] = attrs.Count > 0 ? attrs : null,
                ["file"] = span is { } s1 ? Rel(s1.Path) : null, ["line"] = span is { } s2 ? s2.StartLinePosition.Line + 1 : null,
            }, 150);
        }
    }

    private static void AddDoc(GraphNode n, ISymbol s)
    {
        var summary = Regex.Match(s.GetDocumentationCommentXml() ?? "", "<summary>(?<s>.*?)</summary>", RegexOptions.Singleline);
        if (summary.Success) n["doc"] = Text.OneLine(summary.Groups["s"].Value, 500);
    }

    private static bool IsTestProject(string name) =>
        Regex.IsMatch(name, @"(^|[.\-_])(Tests?|UnitTests?|IntegrationTests?|Specs?)($|[.\-_])", RegexOptions.IgnoreCase);

    private void Info(string msg) { Console.Error.WriteLine(msg); _log.Add(msg); }
    private void Warn(string msg) => Info("[warn] " + msg);
}
