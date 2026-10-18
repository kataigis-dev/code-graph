using System.Text.Json;

namespace RoslynGraph;

/// <summary>Proprieta' di un nodo o di un arco. Assegnare null rimuove la proprieta'.</summary>
public abstract record GraphItem
{
    public Dictionary<string, object?> Props { get; } = new();

    public object? this[string key]
    {
        get => Props.GetValueOrDefault(key);
        set { if (value is null) Props.Remove(key); else Props[key] = value; }
    }

    /// <summary>I flag si scrivono solo quando sono veri.</summary>
    public void Flag(string key, bool on) { if (on) Props[key] = true; }

    /// <summary>Aggiunge un valore a una lista di stringhe senza duplicati, con un limite massimo.</summary>
    public void AddDistinct(string key, string value, int cap = 200) => Append(key, value, cap);

    /// <summary>Aggiunge un oggetto strutturato a una lista (validazioni, eccezioni lanciate).</summary>
    public void AddItem(string key, Dictionary<string, object?> item, int cap) => Append(key, item, cap);

    private void Append<T>(string key, T value, int cap)
    {
        if (this[key] is not List<T> list) Props[key] = list = new List<T>();
        if (list.Count < cap && !list.Contains(value)) list.Add(value);
    }
}

/// <summary>Tipo, metodo, rotta, vista, pagina, tabella, stored procedure o progetto.</summary>
public sealed record GraphNode(string Id, string Kind, string Name) : GraphItem;

/// <summary>Arco orientato tipizzato (CALLS, READS, HANDLED_BY, ...).</summary>
public sealed record GraphEdge(string From, string To, string Type) : GraphItem;

public sealed class CodeGraph
{
    private readonly Dictionary<string, GraphNode> _nodes = new();
    private readonly Dictionary<(string, string, string), GraphEdge> _edges = new();

    public Dictionary<string, object?> Meta { get; } = new();
    public IReadOnlyDictionary<string, GraphNode> Nodes => _nodes;
    public IEnumerable<GraphEdge> Edges => _edges.Values;
    public int EdgeCount => _edges.Count;

    public bool Has(string id) => _nodes.ContainsKey(id);

    public GraphNode? Get(string id) => _nodes.GetValueOrDefault(id);

    public GraphNode Node(string id, string kind, string name) =>
        _nodes.TryGetValue(id, out var n) ? n : _nodes[id] = new GraphNode(id, kind, name);

    /// <summary>Archi uguali (stessi estremi e tipo) non si duplicano ma si contano; la riga e' quella della prima occorrenza.</summary>
    public GraphEdge Edge(string from, string to, string type, int? line = null)
    {
        if (!_edges.TryGetValue((from, to, type), out var e))
        {
            _edges[(from, to, type)] = e = new GraphEdge(from, to, type);
            e["count"] = 0;
            e["line"] = line;
        }
        e["count"] = (int)e["count"]! + 1;
        return e;
    }

    /// <summary>Rimuove gli archi verso nodi mai creati (accessor di auto-property, codice generato escluso).</summary>
    public int PruneDanglingEdges()
    {
        var dangling = _edges.Where(kv => !Has(kv.Value.From) || !Has(kv.Value.To)).Select(kv => kv.Key).ToList();
        dangling.ForEach(k => _edges.Remove(k));
        return dangling.Count;
    }

    public void WriteJson(string path)
    {
        using var fs = File.Create(path);
        JsonSerializer.Serialize(fs, new
        {
            meta = Meta,
            nodes = _nodes.Values.Select(n => Flat(n, ("id", n.Id), ("kind", n.Kind), ("name", n.Name))),
            edges = _edges.Values.Select(e => Flat(e, ("from", e.From), ("to", e.To), ("type", e.Type))),
        });
    }

    private static Dictionary<string, object?> Flat(GraphItem item, params (string Key, object? Value)[] head)
    {
        var d = head.ToDictionary(h => h.Key, h => h.Value);
        foreach (var (k, v) in item.Props) d[k] = v;
        return d;
    }

    /// <summary>CSV importabili con `neo4j-admin database import` o LOAD CSV.</summary>
    public void WriteNeo4jCsv(string dir)
    {
        Directory.CreateDirectory(dir);
        WriteLines(Path.Combine(dir, "nodes.csv"), "id:ID,:LABEL,name,props",
                   _nodes.Values.Select(n => Csv(n.Id, n.Kind, n.Name, JsonSerializer.Serialize(n.Props))));
        WriteLines(Path.Combine(dir, "edges.csv"), ":START_ID,:END_ID,:TYPE,count:int,line:int",
                   _edges.Values.Select(e => $"{Csv(e.From, e.To, e.Type)},{e["count"]},{e["line"]}"));
    }

    private static void WriteLines(string path, string header, IEnumerable<string> rows) =>
        File.WriteAllText(path, string.Join("\n", rows.Prepend(header)) + "\n");

    private static string Csv(params string[] fields) => string.Join(',', fields.Select(f => "\"" + f.Replace("\"", "\"\"") + "\""));
}
