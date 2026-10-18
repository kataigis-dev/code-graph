using System.Text.RegularExpressions;

namespace RoslynGraph;

/// <summary>
/// Euristiche sulle stringhe SQL nel codice (ADO.NET, Dapper, query inline).
/// Sono euristiche: gli archi prodotti portano via = "sql" e vanno trattati come DEDOTTI, non CONFERMATI.
/// </summary>
public static class SqlHeuristics
{
    private const RegexOptions O = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

    // identificatore SQL: tabella, schema.tabella, db.schema.tabella, con o senza parentesi quadre
    private const string Ident = @"(?<t>\[?[A-Za-z_#@][\w$#@]*\]?(?:\s*\.\s*\[?[A-Za-z_][\w$#@]*\]?){0,2})";

    private static readonly Regex SelectRx = new(@"\bselect\s+(?:top\s*\(?\s*\d+\s*\)?\s+)?(?:distinct\s+)?(?:\*|count\s*\(|[\w\[\]\.]+\s*(?:,|\bfrom\b|\bas\b))", O);
    private static readonly Regex FromRx = new(@"\b(?:from|join)\s+" + Ident, O);
    private static readonly Regex InsertRx = new(@"\binsert\s+(?:into\s+)?" + Ident + @"\s*(?:\(|\bvalues\b|\bselect\b|\boutput\b)", O);
    private static readonly Regex UpdateRx = new(@"\bupdate\s+" + Ident + @"\s+set\b", O);
    private static readonly Regex DeleteRx = new(@"\bdelete\s+from\s+" + Ident, O);
    private static readonly Regex MergeRx = new(@"\bmerge\s+(?:into\s+)?" + Ident + @"\s+(?:as\s+\w+\s+)?using\b", O);
    private static readonly Regex ExecRx = new(@"(?:^\s*|;\s*)exec(?:ute)?\s+(?:@\w+\s*=\s*)?" + Ident, O);
    private static readonly Regex ProcNameRx = new(@"^\s*(?:\[?\w+\]?\.)?\[?[A-Za-z_]\w*\]?\s*$", O);

    private static readonly HashSet<string> NotTables = new(StringComparer.OrdinalIgnoreCase)
    {
        "select", "where", "set", "values", "with", "on", "as", "into", "from", "table", "inserted", "deleted",
        "dual", "openjson", "openquery", "string_split", "unnest", "lateral",
    };

    public sealed record Result(bool IsSql, List<string> Reads, List<string> Writes, List<string> Procs);

    public static Result Analyze(string text)
    {
        var writes = Names(text, InsertRx, UpdateRx, DeleteRx, MergeRx);
        var procs = Names(text, ExecRx);
        bool isSql = SelectRx.IsMatch(text) || writes.Count > 0 || procs.Count > 0;
        // "delete from X" compare anche tra i FROM: non e' una lettura
        var reads = isSql ? Names(text, FromRx).Where(r => !writes.Contains(r)).ToList() : new List<string>();
        return new Result(isSql, reads, writes, procs);
    }

    public static bool LooksLikeProcName(string text) =>
        text.Length is > 2 and < 128 && ProcNameRx.IsMatch(text) && !text.Trim().All(char.IsDigit);

    /// <summary>Normalizza per l'id del nodo: senza parentesi, minuscolo, senza schema dbo.</summary>
    public static string NormalizeName(string raw)
    {
        var s = Regex.Replace(raw, @"[\[\]`""\s]", "").ToLowerInvariant();
        var parts = s.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 3) parts = parts[1..];          // db.schema.tabella -> schema.tabella
        if (parts.Length == 2 && parts[0] == "dbo") parts = parts[1..];
        return string.Join('.', parts);
    }

    private static List<string> Names(string text, params Regex[] patterns)
    {
        var names = new List<string>();
        foreach (var m in patterns.SelectMany(rx => rx.Matches(text)))
        {
            var name = Regex.Replace(m.Groups["t"].Value, @"[\[\]\s]", "");
            // @t e #t: variabili tabella e tabelle temporanee
            if (name.Length > 0 && name[0] is not ('@' or '#') && !NotTables.Contains(name)
                && !names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);
        }
        return names;
    }
}

/// <summary>Espressioni regolari per le viste Razor (.cshtml). \x22 = virgolette doppie.</summary>
public static class RazorPatterns
{
    private const RegexOptions O = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

    public static readonly Regex Model = new(@"^\s*@model\s+(?<t>[^\r\n]+)", O | RegexOptions.Multiline);
    public static readonly Regex ActionLink = new(@"Html\.ActionLink\(\s*[^,]+?,\s*\x22(?<a>\w+)\x22(?:\s*,\s*\x22(?<c>\w+)\x22)?", O);
    public static readonly Regex UrlAction = new(@"Url\.Action\(\s*\x22(?<a>\w+)\x22(?:\s*,\s*\x22(?<c>\w+)\x22)?", O);
    public static readonly Regex BeginForm = new(@"(?:Html|Ajax)\.BeginForm\(\s*\x22(?<a>\w+)\x22(?:\s*,\s*\x22(?<c>\w+)\x22)?", O);
    public static readonly Regex ChildAction = new(@"Html\.(?:Render)?Action\(\s*\x22(?<a>\w+)\x22(?:\s*,\s*\x22(?<c>\w+)\x22)?", O);
    public static readonly Regex Partial = new(@"(?:Html\.(?:Render)?Partial(?:Async)?\(\s*|<partial\s+name\s*=\s*)\x22(?<p>[^\x22]+)\x22", O);
    public static readonly Regex Layout = new(@"Layout\s*=\s*\x22(?<l>[^\x22]+)\x22", O);
    public static readonly Regex BoundField = new(@"\.(?<h>\w+For)\(\s*\w+\s*=>\s*\w+\.(?<f>[\w\.]+)", O);
    public static readonly Regex ViewBag = new(@"ViewBag\.(?<k>\w+)", O);
    public static readonly Regex Role = new(@"IsInRole\(\s*\x22(?<r>[^\x22]+)\x22", O);
    public static readonly Regex AjaxUrl = new(@"(?:url\s*:\s*|\$\.(?:get|post|getJSON|ajax)\(\s*|fetch\(\s*)['\x22](?<u>/[^'\x22]+)['\x22]", O);
    public static readonly Regex Conditional = new(@"@(?:if|switch)\s*\(", O);
    public static readonly Regex TypeToken = new(@"[A-Za-z_][\w\.]*", O);

    public static readonly HashSet<string> GenericWrappers = new(StringComparer.Ordinal)
    {
        "IEnumerable", "List", "IList", "ICollection", "IQueryable", "IReadOnlyList", "IReadOnlyCollection",
        "IPagedList", "PagedList", "dynamic", "System", "Collections", "Generic", "string", "int", "object",
    };
}

/// <summary>
/// Espressioni regolari per il markup Web Forms (.aspx, .ascx, .master, .asmx, .ashx, .asax).
/// I valori degli attributi possono contenere espressioni &lt;%# ... %&gt; con il carattere '&gt;': per questo i tag
/// si leggono saltando le stringhe tra virgolette.
/// </summary>
public static class WebFormsPatterns
{
    private const RegexOptions O = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;
    private const string Attrs = @"(?<attrs>(?:[^>""']|""[^""]*""|'[^']*')*)";

    public static readonly Regex Directive = new(@"<%@\s*(?<d>\w+)(?<attrs>(?:[^%""']|""[^""]*""|'[^']*'|%(?!>))*)%>", O);
    public static readonly Regex Tag = new(@"<(?<name>[A-Za-z][\w:.-]*)" + Attrs + ">", O);
    public static readonly Regex Attribute = new(@"(?<k>[\w:.-]+)\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)')", O);
    public static readonly Regex Identifier = new(@"^[A-Za-z_]\w*$", O);
    public static readonly Regex PageUrl = new(@"\b(?<attr>NavigateUrl|PostBackUrl|href|action)\s*=\s*[""'](?<u>[^""'<>]*?\.as[hp]x)(?=[""'?#])", O);
    public static readonly Regex InlineRedirect = new(@"(?:Response\.Redirect(?:Permanent)?|Server\.(?:Transfer|Execute))\(\s*""(?<u>[^""]*?\.as[hp]x)", O);
    public static readonly Regex EvalBind = new(@"\b(?<k>Eval|Bind)\(\s*""(?<f>[^""]+)""", O);
    public static readonly Regex StateKey = new(@"\b(?<s>Session|ViewState|Application|Cache)\[\s*""(?<k>[^""]+)""\s*\]", O);
    public static readonly Regex RequestKey = new(@"\bRequest(?:\.(?<s>QueryString|Form|Params))?\[\s*""(?<k>[^""]+)""\s*\]", O);
    public static readonly Regex ConnectionString = new(@"<%\$\s*ConnectionStrings\s*:\s*(?<n>[\w.]+)", O);
    public static readonly Regex InlineCode = new(@"<%(?![@#=:$]|--)", O);
    public static readonly Regex ServerScript = new(@"<script\b[^>]*\brunat\s*=\s*[""']?server", O);
}
