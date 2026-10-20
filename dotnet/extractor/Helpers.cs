using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynGraph;

/// <summary>Fatti sui simboli: attributi, autorizzazioni, controller, Entity Framework.</summary>
internal static class SymbolExtensions
{
    public static string AttrName(this AttributeData a)
    {
        var s = a.AttributeClass?.Name ?? "";
        return s.EndsWith("Attribute", StringComparison.Ordinal) ? s[..^"Attribute".Length] : s;
    }

    public static bool HasAttr(this ISymbol s, string name) => s.GetAttributes().Any(a => a.AttrName() == name);

    /// <summary>Primo argomento stringa di un attributo; ripiega sulla sintassi se il tipo dell'attributo non e' risolto.</summary>
    public static string? AttrString(this ISymbol s, string name)
    {
        foreach (var a in s.GetAttributes().Where(a => a.AttrName() == name))
        {
            if (a.ConstructorArguments is [{ Value: string v }, ..]) return v;
            if (a.ApplicationSyntaxReference?.GetSyntax() is AttributeSyntax { ArgumentList.Arguments: [var first, ..] }
                && first.Expression is LiteralExpressionSyntax lit)
                return lit.Token.ValueText;
        }
        return null;
    }

    /// <summary>
    /// Attributi dichiarati piu' quelli ereditati a runtime: dai tipi base per una classe, dai metodi sovrascritti
    /// per un metodo. Senza questo un [Authorize] su un BaseController non comparirebbe sulle rotte dei controller derivati.
    /// </summary>
    public static IEnumerable<(AttributeData Attr, string? From)> EffectiveAttributes(this ISymbol s)
    {
        foreach (var a in s.GetAttributes()) yield return (a, null);
        if (s is INamedTypeSymbol t)
            for (var b = t.BaseType; b is not null; b = b.BaseType)
                foreach (var a in b.GetAttributes().Where(IsInherited)) yield return (a, b.Name);
        if (s is IMethodSymbol m)
            for (var o = m.OverriddenMethod; o is not null; o = o.OverriddenMethod)
                foreach (var a in o.GetAttributes().Where(IsInherited)) yield return (a, $"{o.ContainingType.Name}.{o.Name}");
    }

    /// <summary>AttributeUsage(Inherited = ...) della classe dell'attributo; senza AttributeUsage (o non risolto) vale true.</summary>
    private static bool IsInherited(AttributeData a)
    {
        for (var c = a.AttributeClass; c is not null; c = c.BaseType)
            if (c.GetAttributes().FirstOrDefault(u => u.AttributeClass?.Name == "AttributeUsageAttribute") is { } usage)
                return usage.NamedArguments.FirstOrDefault(kv => kv.Key == "Inherited").Value.Value is not false;
        return true;
    }

    /// <summary>[Authorize] e [AllowAnonymous] effettivi; "[ereditato da X]" indica il tipo o il metodo base da cui arrivano.</summary>
    public static List<string> Authorization(this ISymbol s)
    {
        var result = new List<string>();
        foreach (var (a, inheritedFrom) in s.EffectiveAttributes())
        {
            var name = a.AttrName();
            var origin = inheritedFrom is null ? "" : $" [ereditato da {inheritedFrom}]";
            if (name == "AllowAnonymous") result.Add(name + origin);
            // WCF e codice .NET Framework: [PrincipalPermission(SecurityAction.Demand, Role = "Admin")]
            else if (name == "PrincipalPermission")
                result.Add($"{name}({string.Join(", ", a.NamedArguments.Select(kv => $"{kv.Key}={kv.Value.Value}"))})" + origin);
            else if (name.EndsWith("Authorize", StringComparison.Ordinal))
            {
                var args = a.NamedArguments.Select(kv => $"{kv.Key}={kv.Value.Value}").ToList();
                if (args.Count == 0 && a.ApplicationSyntaxReference?.GetSyntax() is AttributeSyntax { ArgumentList: { } list })
                    args.Add(list.ToString());
                result.Add((args.Count > 0 ? $"{name}({string.Join(", ", args)})" : name) + origin);
            }
        }
        return result;
    }

    /// <summary>Extension method chiamati come istanza e generici costruiti diventano la loro definizione.</summary>
    public static IMethodSymbol Norm(this IMethodSymbol m) => (m.ReducedFrom ?? m).OriginalDefinition;

    public static string DisplayName(this IMethodSymbol m) => m.ContainingType.Name + m.MethodKind switch
    {
        MethodKind.Constructor or MethodKind.StaticConstructor => ".ctor",
        MethodKind.PropertyGet => $".{m.AssociatedSymbol?.Name}.get",
        MethodKind.PropertySet => $".{m.AssociatedSymbol?.Name}.set",
        _ => "." + m.Name,
    };

    /// <summary>Il tipo con i suoi argomenti generici e gli elementi degli array, ricorsivamente.</summary>
    public static IEnumerable<INamedTypeSymbol> Flatten(this ITypeSymbol t) => t switch
    {
        IArrayTypeSymbol a => a.ElementType.Flatten(),
        INamedTypeSymbol n => n.TypeArguments.SelectMany(Flatten).Prepend(n),
        _ => [],
    };

    /// <summary>Entita' di un DbSet/IDbSet (EF6, EF Core) o ObjectSet/IObjectSet (EF4 con ObjectContext, tipico di Web Forms).</summary>
    public static ITypeSymbol? DbSetEntity(this ITypeSymbol t) =>
        t is INamedTypeSymbol { Name: "DbSet" or "IDbSet" or "ObjectSet" or "IObjectSet", TypeArguments: [var entity] } ? entity : null;

    /// <summary>Il tipo e' un contesto EF o ne deriva. Include il tipo stesso: Set&lt;T&gt;() e' dichiarato su DbContext.</summary>
    public static bool IsDbContext(this INamedTypeSymbol? t)
    {
        for (; t is not null; t = t.BaseType)
            if (t.Name is "DbContext" or "ObjectContext" or "IdentityDbContext") return true;
        return false;
    }

    public static string TableName(this ITypeSymbol entity, string? fallback)
    {
        var attr = entity.GetAttributes().FirstOrDefault(a => a.AttrName() == "Table");
        if (attr is not { ConstructorArguments: [{ Value: string table }, ..] }) return fallback ?? entity.Name;
        return attr.NamedArguments.FirstOrDefault(kv => kv.Key == "Schema").Value.Value is string schema ? $"{schema}.{table}" : table;
    }

    /// <summary>mvc, webapi, aspnetcore oppure convention (base non risolta: decide il nome); null se non e' un controller.</summary>
    public static string? ControllerFlavor(this INamedTypeSymbol t)
    {
        for (var b = t.BaseType; b is not null; b = b.BaseType)
        {
            var flavor = b.ToDisplayString() switch
            {
                "System.Web.Mvc.Controller" => "mvc",
                "System.Web.Http.ApiController" => "webapi",
                "Microsoft.AspNetCore.Mvc.Controller" or "Microsoft.AspNetCore.Mvc.ControllerBase" => "aspnetcore",
                _ => null,
            };
            if (flavor is not null) return t.IsAbstract ? null : flavor;
        }
        return t is { TypeKind: TypeKind.Class, IsAbstract: false, DeclaredAccessibility: Accessibility.Public }
               && t.Name.EndsWith("Controller", StringComparison.Ordinal) ? "convention" : null;
    }

    public static string? Area(this INamedTypeSymbol t)
    {
        var m = Regex.Match(t.ContainingNamespace?.ToDisplayString() ?? "", @"\.Areas\.(?<a>\w+)");
        return t.AttrString("RouteArea") ?? (m.Success ? m.Groups["a"].Value : null);
    }

    // System.Web.Mvc.HttpVerbs: un bit per verbo, in quest'ordine
    private static readonly string[] HttpVerbs = { "GET", "POST", "PUT", "DELETE", "HEAD", "PATCH", "OPTIONS" };

    public static List<string> Verbs(this IMethodSymbol m, string flavor)
    {
        var verbs = new List<string>();
        foreach (var a in m.GetAttributes())
        {
            var name = a.AttrName();
            if (name.StartsWith("Http", StringComparison.Ordinal) && name.Length > 4) verbs.Add(name[4..].ToUpperInvariant());
            else if (name == "AcceptVerbs")
                foreach (var arg in a.ConstructorArguments)
                {
                    if (arg.Kind == TypedConstantKind.Array)
                        verbs.AddRange(arg.Values.Select(v => v.Value?.ToString()?.ToUpperInvariant() ?? ""));
                    else if (arg.Value is int flags) verbs.AddRange(HttpVerbs.Where((_, i) => (flags & (1 << i)) != 0));
                    else if (arg.Value is string s) verbs.Add(s.ToUpperInvariant());
                }
        }
        // Web API senza attributi: il verbo e' il prefisso del nome (GetOrders, PostOrder)
        var byName = new[] { "Get", "Post", "Put", "Delete", "Patch" }.FirstOrDefault(v => m.Name.StartsWith(v, StringComparison.Ordinal));
        if (verbs.Count == 0 && flavor == "webapi" && byName is not null) verbs.Add(byName.ToUpperInvariant());
        if (verbs.Count == 0) verbs.Add("ANY");
        return verbs.Where(v => v.Length > 0).Distinct().ToList();
    }

    /// <summary>
    /// Session, Cache, Request, ... contano come stato implicito solo se vengono da System.Web o ASP.NET Core:
    /// un ISession di NHibernate o un parametro chiamato Cache no. Simbolo non risolto (riferimenti mancanti): ci si fida del nome.
    /// </summary>
    public static bool IsWebState(this ISymbol? s) => s switch
    {
        null => true,
        IPropertySymbol p => IsWeb(p.ContainingType) || IsWeb(p.Type),
        IFieldSymbol f => IsWeb(f.ContainingType) || IsWeb(f.Type),
        _ => false,
    };

    private static bool IsWeb(ITypeSymbol? t) =>
        t?.ContainingNamespace?.ToDisplayString() is { } ns
        && (ns.StartsWith("System.Web", StringComparison.Ordinal) || ns.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
}

/// <summary>Forme ricorrenti della sintassi C#.</summary>
internal static class SyntaxExtensions
{
    public static int Line(this SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    /// <summary>Simbolo dell'espressione oppure, se la risoluzione e' ambigua, il primo candidato.</summary>
    public static ISymbol? SymbolOf(this SemanticModel model, SyntaxNode node)
    {
        var si = model.GetSymbolInfo(node);
        return si.Symbol ?? si.CandidateSymbols.FirstOrDefault();
    }

    public static SyntaxNode? Body(this SyntaxNode decl) => decl switch
    {
        BaseMethodDeclarationSyntax m => (SyntaxNode?)m.Body ?? m.ExpressionBody,
        AccessorDeclarationSyntax a => (SyntaxNode?)a.Body ?? a.ExpressionBody,
        _ => null,
    };

    public static string InvokedName(this InvocationExpressionSyntax inv) => inv.Expression switch
    {
        IdentifierNameSyntax i => i.Identifier.ValueText,
        GenericNameSyntax g => g.Identifier.ValueText,
        MemberAccessExpressionSyntax ma => ma.Name.Identifier.ValueText,
        MemberBindingExpressionSyntax mb => mb.Name.Identifier.ValueText,
        _ => "",
    };

    public static string? LiteralArg(this InvocationExpressionSyntax inv, int index) =>
        inv.ArgumentList.Arguments.Count > index
        && inv.ArgumentList.Arguments[index].Expression is LiteralExpressionSyntax l && l.IsKind(SyntaxKind.StringLiteralExpression)
            ? l.Token.ValueText : null;

    /// <summary>Parte letterale iniziale di un URL: "~/X.aspx?id=" + id, $"~/X.aspx?id={id}".</summary>
    public static string? LeadingLiteral(this ExpressionSyntax e) => e switch
    {
        LiteralExpressionSyntax l when l.IsKind(SyntaxKind.StringLiteralExpression) => l.Token.ValueText,
        BinaryExpressionSyntax b when b.IsKind(SyntaxKind.AddExpression) => b.Left.LeadingLiteral(),
        InterpolatedStringExpressionSyntax i => string.Concat(i.Contents.TakeWhile(c => c is InterpolatedStringTextSyntax)
                                                                  .Select(c => ((InterpolatedStringTextSyntax)c).TextToken.ValueText)),
        ParenthesizedExpressionSyntax p => p.Expression.LeadingLiteral(),
        _ => null,
    };

    /// <summary>db.Orders.Add(x) / Orders.Remove(x) / db.Set&lt;T&gt;().Add(x) -> scrittura; tutto il resto -> lettura.</summary>
    public static bool IsDbSetWrite(this ExpressionSyntax expr)
    {
        SyntaxNode cur = expr;
        if (cur.Parent is MemberAccessExpressionSyntax inner && inner.Name == cur) cur = inner;
        return cur.Parent is MemberAccessExpressionSyntax outer && outer.Expression == cur
               && outer.Name.Identifier.ValueText is "Add" or "AddRange" or "Remove" or "RemoveRange" or "Attach"
                   or "AddOrUpdate" or "Update" or "UpdateRange" or "AddAsync" or "AddRangeAsync";
    }

    public static bool IsAssignmentTarget(this IdentifierNameSyntax idn)
    {
        SyntaxNode cur = idn;
        if (cur.Parent is MemberAccessExpressionSyntax ma && ma.Name == cur) cur = ma;
        return cur.Parent is AssignmentExpressionSyntax asg && asg.Left == cur;
    }

    /// <summary>Stringa passata a un costruttore o a un metodo, oppure assegnata a CommandText.</summary>
    public static bool IsCommandText(this LiteralExpressionSyntax lit) =>
        lit.Parent is ArgumentSyntax { Parent.Parent: BaseObjectCreationExpressionSyntax or InvocationExpressionSyntax }
        || (lit.Parent is AssignmentExpressionSyntax asg && asg.Left.ToString().EndsWith("CommandText", StringComparison.Ordinal));

    /// <summary>Gestore di un evento: btnSave_Click, oppure new EventHandler(btnSave_Click).</summary>
    public static IMethodSymbol? HandlerOf(this SemanticModel model, ExpressionSyntax right)
    {
        if (right is BaseObjectCreationExpressionSyntax { ArgumentList.Arguments: [var arg] }) right = arg.Expression;
        return model.SymbolOf(right) as IMethodSymbol;
    }
}

/// <summary>Percorsi di file e URL.</summary>
internal static class Paths
{
    public static string Combine(string folder, string rel) => folder.Length == 0 ? rel : $"{folder}/{rel}";

    public static string FolderOf(string path) => path.Contains('/') ? path[..path.LastIndexOf('/')] : "";

    public static string JoinUrl(string? prefix, string route) =>
        string.IsNullOrEmpty(prefix) ? route : $"{prefix.TrimEnd('/')}/{route.TrimStart('/')}";

    /// <summary>Risolve "." e ".."; null se il percorso esce dalla radice o e' vuoto.</summary>
    public static string? Normalize(string path)
    {
        var parts = new List<string>();
        foreach (var seg in path.Split('/'))
        {
            if (seg is "" or ".") continue;
            if (seg != "..") parts.Add(seg);
            else if (parts.Count == 0) return null;
            else parts.RemoveAt(parts.Count - 1);
        }
        return parts.Count == 0 ? null : string.Join('/', parts);
    }

    public static bool IsExcluded(string? path, string? projectDir) =>
        string.IsNullOrEmpty(path)
        || Matches(ProjectRelative(path, projectDir), contains: ["/obj/", "/bin/", "TemporaryGeneratedFile"],
                   endsWith: [".g.cs", ".g.i.cs", "AssemblyInfo.cs"]);

    public static bool IsGenerated(string? path, string? projectDir) =>
        !string.IsNullOrEmpty(path) && Matches(ProjectRelative(path, projectDir), contains: ["/Migrations/"],
                                               endsWith: [".designer.cs", "/Reference.cs"]);

    private static bool Matches(string path, string[] contains, string[] endsWith) =>
        contains.Any(c => path.Contains(c, StringComparison.OrdinalIgnoreCase))
        || endsWith.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Percorso relativo alla cartella del progetto, con '/' iniziale. I controlli su bin/obj/Migrations guardano solo
    /// dentro il progetto: una solution in C:\bin\... non deve sparire dal grafo. Fuori dal progetto conta solo il nome del file.
    /// </summary>
    private static string ProjectRelative(string path, string? projectDir)
    {
        var rel = projectDir is null ? Path.GetFileName(path) : Path.GetRelativePath(projectDir, path);
        if (rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel)) rel = Path.GetFileName(path);
        return "/" + rel.Replace('\\', '/');
    }
}

internal static class Text
{
    public static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    /// <summary>Testo su una riga sola, troncato.</summary>
    public static string OneLine(string s, int max) => Truncate(Regex.Replace(s, @"\s+", " ").Trim(), max);
}

internal static class CollectionExtensions
{
    /// <summary>Lista associata alla chiave, creata vuota se manca.</summary>
    public static List<TValue> Bucket<TKey, TValue>(this Dictionary<TKey, List<TValue>> d, TKey key) where TKey : notnull =>
        d.TryGetValue(key, out var list) ? list : d[key] = new List<TValue>();
}
