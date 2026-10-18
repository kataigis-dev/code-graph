using System.Text.RegularExpressions;

namespace RoslynGraph;

/// <summary>Passata 4: viste Razor (.cshtml). Modello, campi legati, link e form verso le rotte, partial, layout.</summary>
public sealed partial class Extractor
{
    private readonly Dictionary<string, string> _viewIndex = new(StringComparer.OrdinalIgnoreCase); // "progetto|percorso" -> view id
    private readonly List<PendingView> _pendingViews = new();
    private readonly List<PendingRedirect> _pendingRedirects = new();
    private static readonly string[] GetVerb = ["GET"], PostVerb = ["POST"];

    private sealed record PendingView(string FromMethod, Controller Controller, string Name, bool Partial, int Line);
    private sealed record PendingRedirect(string FromMethod, string Controller, string Action, int Line);

    private void PassRazor()
    {
        var views = new List<SourceFile>();
        foreach (var (p, _) in _units)
            foreach (var (full, projRel) in ProjectFiles(p, f => f.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase)))
                if (AddFileNode(p, full, projRel, "V:", "View", Path.GetFileNameWithoutExtension(full)) is { } vf)
                {
                    _g.Get(vf.Id)!.Flag("partial", Path.GetFileName(full).StartsWith('_'));
                    _viewIndex[$"{p.Name}|{projRel}"] = vf.Id;
                    views.Add(vf);
                }

        var typesByName = _types.ToLookup(t => t.Type.Name, t => Id(t.Type)!);
        foreach (var vf in views) ParseView(vf, typesByName);
        foreach (var v in _pendingViews)
            _g.Edge(v.FromMethod, ResolveView(v.Controller.Project, v.Controller.Name, v.Controller.Area, v.Name, null), "RENDERS", v.Line)
              .Flag("partial", v.Partial);
        // il browser segue un redirect con una GET
        foreach (var r in _pendingRedirects)
            LinkToRoutes(r.FromMethod, r.Controller, r.Action, GetVerb, "REDIRECTS_TO", r.Line, "unresolved_redirects");
    }

    private void ParseView(SourceFile vf, ILookup<string, string> typesByName)
    {
        if (TryRead(vf, "vista") is not { } text) return;
        var n = _g.Get(vf.Id)!;
        n["loc"] = text.Count(c => c == '\n') + 1;
        n["conditionals"] = RazorPatterns.Conditional.Matches(text).Count;
        n["scripts"] = Regex.Matches(text, "<script", RegexOptions.IgnoreCase).Count;
        n["forms"] = Regex.Matches(text, @"BeginForm|<form\b", RegexOptions.IgnoreCase).Count;

        var (area, ctrl) = ControllerFromViewPath(vf.ProjRel);
        var folder = Paths.FolderOf(vf.ProjRel);
        if (RazorPatterns.Model.Match(text) is { Success: true } mm)
        {
            var model = mm.Groups["t"].Value.Trim();
            n["model"] = model;
            foreach (var typeId in ResolveTypeNames(model, typesByName)) _g.Edge(vf.Id, typeId, "USES_MODEL");
        }

        LinkRoutes(vf, n, text, RazorPatterns.ActionLink, "LINKS_TO", ctrl, GetVerb);
        LinkRoutes(vf, n, text, RazorPatterns.UrlAction, "LINKS_TO", ctrl, null); // Url.Action serve anche per POST ajax
        LinkRoutes(vf, n, text, RazorPatterns.BeginForm, "POSTS_TO", ctrl, PostVerb);
        LinkRoutes(vf, n, text, RazorPatterns.ChildAction, "RENDERS_ACTION", ctrl, GetVerb);
        foreach (Match m in RazorPatterns.Partial.Matches(text))
            _g.Edge(vf.Id, ResolveView(vf.Project, ctrl, area, m.Groups["p"].Value, folder), "RENDERS_PARTIAL");
        if (RazorPatterns.Layout.Match(text) is { Success: true } lm)
            _g.Edge(vf.Id, ResolveView(vf.Project, ctrl, area, lm.Groups["l"].Value, folder), "USES_LAYOUT");

        foreach (Match m in RazorPatterns.BoundField.Matches(text))
            n.AddDistinct("bound_fields", $"{m.Groups["f"].Value} ({m.Groups["h"].Value})", 300);
        foreach (Match m in RazorPatterns.ViewBag.Matches(text)) n.AddDistinct("viewbag_keys", m.Groups["k"].Value);
        foreach (Match m in RazorPatterns.Role.Matches(text)) n.AddDistinct("roles_checked", m.Groups["r"].Value);
        foreach (Match m in RazorPatterns.AjaxUrl.Matches(text)) n.AddDistinct("ajax_urls", m.Groups["u"].Value);
    }

    private void LinkRoutes(SourceFile vf, GraphNode n, string text, Regex rx, string edgeType, string? currentCtrl, string[]? verbs)
    {
        foreach (Match m in rx.Matches(text))
        {
            var action = m.Groups["a"].Value;
            var ctrl = m.Groups["c"].Success ? m.Groups["c"].Value : currentCtrl;
            if (ctrl is null) { n.AddDistinct("unresolved_links", $"?/{action}"); continue; }
            // BeginForm(..., FormMethod.Get) invia il form con una GET
            var end = text.IndexOf('\n', m.Index);
            var restOfLine = text[m.Index..(end < 0 ? text.Length : end)];
            bool formGet = edgeType == "POSTS_TO" && restOfLine.Contains("FormMethod.Get", StringComparison.Ordinal);
            LinkToRoutes(vf.Id, ctrl, action, formGet ? GetVerb : verbs, edgeType, null, "unresolved_links");
        }
    }

    /// <summary>Archi verso le rotte di controller/action; se non ce ne sono, "controller/action" va in unresolvedKey.</summary>
    private void LinkToRoutes(string from, string ctrl, string action, string[]? verbs, string edgeType, int? line, string unresolvedKey)
    {
        var routes = RoutesFor(ctrl, action, verbs);
        foreach (var r in routes) _g.Edge(from, r, edgeType, line);
        if (routes.Count == 0) _g.Get(from)?.AddDistinct(unresolvedKey, $"{ctrl}/{action}");
    }

    /// <summary>
    /// Rotte di controller/action compatibili con i verbi (ANY e' sempre compatibile): un BeginForm non deve
    /// puntare alla Create in GET. Se nessuna e' compatibile restituisce tutte le candidate.
    /// </summary>
    private List<string> RoutesFor(string ctrl, string action, string[]? verbs)
    {
        var routes = _routesByAction.GetValueOrDefault($"{ctrl}/{action}") ?? new List<string>();
        if (verbs is null) return routes;
        var matching = routes.Where(r => _g.Get(r)?["verbs"] is List<string> v && (v.Contains("ANY") || v.Intersect(verbs).Any())).ToList();
        return matching.Count > 0 ? matching : routes;
    }

    /// <summary>Regole di ricerca delle viste di MVC 5; se il file non c'e' crea un nodo segnaposto.</summary>
    private string ResolveView(string project, string? ctrl, string? area, string name, string? currentFolder)
    {
        var candidates = new List<string>();
        if (name.StartsWith("~/", StringComparison.Ordinal)) candidates.Add(name[2..]);
        else if (name.Contains('/')) candidates.Add(name.TrimStart('/'));
        else
        {
            var file = name.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase) ? name : name + ".cshtml";
            if (currentFolder is not null) candidates.Add(Paths.Combine(currentFolder, file));
            if (area is not null && ctrl is not null) candidates.Add($"Areas/{area}/Views/{ctrl}/{file}");
            if (ctrl is not null) candidates.Add($"Views/{ctrl}/{file}");
            if (area is not null) candidates.Add($"Areas/{area}/Views/Shared/{file}");
            candidates.Add($"Views/Shared/{file}");
        }
        foreach (var c in candidates)
            if (_viewIndex.TryGetValue($"{project}|{c}", out var id)) return id;

        var missing = _g.Node($"V:?{project}/{candidates[0]}", "View", name);
        missing["unresolved"] = true;
        missing["project"] = project;
        return missing.Id;
    }

    /// <summary>Views/Orders/x.cshtml: controller Orders; Areas/Admin/Views/Users/x.cshtml: area Admin, controller Users.</summary>
    private static (string? Area, string? Ctrl) ControllerFromViewPath(string projRel)
    {
        var seg = projRel.Split('/');
        var vi = Array.FindIndex(seg, s => s.Equals("Views", StringComparison.OrdinalIgnoreCase));
        var ctrl = vi >= 0 && vi + 2 < seg.Length && !seg[vi + 1].Equals("Shared", StringComparison.OrdinalIgnoreCase) ? seg[vi + 1] : null;
        var area = vi >= 2 && seg[vi - 2].Equals("Areas", StringComparison.OrdinalIgnoreCase) ? seg[vi - 1] : null;
        return (area, ctrl);
    }

    /// <summary>Tipi del grafo citati da @model: List&lt;Shop.Web.Models.OrderVm&gt; -> OrderVm.</summary>
    private static IEnumerable<string> ResolveTypeNames(string model, ILookup<string, string> typesByName)
    {
        foreach (Match tok in RazorPatterns.TypeToken.Matches(model))
        {
            var simple = tok.Value.Split('.').Last();
            if (RazorPatterns.GenericWrappers.Contains(simple)) continue;
            var ids = typesByName[simple].ToList();
            var exact = ids.Where(id => id.Split('@')[0].EndsWith(tok.Value, StringComparison.Ordinal)).ToList();
            foreach (var id in exact.Count > 0 ? exact : ids) yield return id;
        }
    }
}
