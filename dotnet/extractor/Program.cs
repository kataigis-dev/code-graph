using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using RoslynGraph;

Options? opt;
try { opt = Options.Parse(args); }
catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); opt = null; }
if (opt is null) { Options.PrintUsage(); return 2; }
if (!File.Exists(opt.Input)) { Console.Error.WriteLine($"file non trovato: {opt.Input}"); return 2; }

// Roslyn >= 4.9: MSBuildWorkspace valuta i progetti in un BuildHost esterno (net472 per i csproj legacy,
// che usa l'MSBuild di Visual Studio/Build Tools). Non serve registrare MSBuild con MSBuildLocator.
var sw = Stopwatch.StartNew();
using var workspace = MSBuildWorkspace.Create(new Dictionary<string, string> { ["Configuration"] = opt.Configuration });
var failures = new List<string>();
#pragma warning disable CS0618 // deprecato nelle versioni recenti, ma resta il modo piu' portabile
workspace.WorkspaceFailed += (_, e) =>
{
    if (e.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure && failures.Count < 200) failures.Add(e.Diagnostic.Message);
};
#pragma warning restore CS0618

Console.Error.WriteLine($"caricamento {opt.Input} ...");
var input = Path.GetFullPath(opt.Input);
var solution = Path.GetExtension(input).ToLowerInvariant() is ".sln" or ".slnx"
    ? await workspace.OpenSolutionAsync(input)
    : (await workspace.OpenProjectAsync(input)).Solution;
foreach (var f in failures.Take(10)) Console.Error.WriteLine("[warn] workspace: " + f);
if (failures.Count > 10)
    Console.Error.WriteLine($"[warn] ... altri {failures.Count - 10} avvisi del workspace (vedi meta.workspaceFailures)");

var root = opt.Root ?? Path.GetDirectoryName(input)!;
var graph = new CodeGraph();
await new Extractor(graph, root, opt).RunAsync(solution);

var counts = graph.Nodes.Values.GroupBy(n => n.Kind).ToDictionary(g => g.Key, g => g.Count());
graph.Meta["tool"] = "roslyn-graph 0.2";
graph.Meta["input"] = Path.GetFileName(input);
graph.Meta["root"] = Path.GetFullPath(root);
graph.Meta["generatedAt"] = DateTimeOffset.Now.ToString("O");
graph.Meta["workspaceFailures"] = failures;
graph.Meta["counts"] = counts;
graph.Meta["edgeCounts"] = graph.Edges.GroupBy(e => e.Type).ToDictionary(g => g.Key, g => g.Count());
graph.WriteJson(opt.Output);
if (opt.Neo4jDir is not null) graph.WriteNeo4jCsv(opt.Neo4jDir);

Console.Error.WriteLine($"fatto in {sw.Elapsed.TotalSeconds:F0}s: {graph.Nodes.Count} nodi, {graph.EdgeCount} archi -> {opt.Output}");
foreach (var (kind, count) in counts) Console.Error.WriteLine($"   {kind,-16} {count}");
return 0;

namespace RoslynGraph
{
    public sealed record Options
    {
        public required string Input { get; init; }
        public string Output { get; init; } = "graph.json";
        public string? Neo4jDir { get; init; }
        public string? Root { get; init; }
        public string Configuration { get; init; } = "Debug";
        public bool IncludeTests { get; init; }

        public static Options? Parse(string[] args)
        {
            if (args is [] or ["-h" or "--help", ..]) return null;
            var o = new Options { Input = args[0] };
            for (int i = 1; i < args.Length; i++)
            {
                string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"manca il valore per {args[i]}");
                o = args[i] switch
                {
                    "--out" => o with { Output = Next() },
                    "--neo4j" => o with { Neo4jDir = Next() },
                    "--root" => o with { Root = Next() },
                    "--configuration" => o with { Configuration = Next() },
                    "--include-tests" => o with { IncludeTests = true },
                    _ => throw new ArgumentException($"opzione sconosciuta: {args[i]}"),
                };
            }
            return o;
        }

        public static void PrintUsage() => Console.Error.WriteLine("""
            roslyn-graph <solution.sln | progetto.csproj> [opzioni]

              --out <file>            file JSON di uscita (default: graph.json)
              --neo4j <cartella>      esporta anche nodes.csv / edges.csv per Neo4j
              --root <cartella>       radice per i percorsi relativi (default: cartella della solution)
              --configuration <cfg>   configurazione MSBuild (default: Debug)
              --include-tests         include i progetti di test

            Prima di lanciarlo: ripristina i pacchetti (nuget restore / msbuild -t:restore),
            altrimenti i simboli esterni non si risolvono e il grafo perde archi.
            """);
    }
}
