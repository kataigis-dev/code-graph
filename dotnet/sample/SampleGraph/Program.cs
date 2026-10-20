// Esegue l'estrattore sull'app d'esempio senza MSBuild:
//   dotnet run --project sample/SampleGraph -- sample/app sample/graph.sample.json --sql sample/app/Database
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using RoslynGraph;

if (args.Length < 2) { Console.Error.WriteLine("uso: SampleGraph <cartella app> <graph.json> [--sql <script o cartella>]..."); return 2; }
var appRoot = Path.GetFullPath(args[0]);
var outPath = Path.GetFullPath(args[1]);
var sql = args.Skip(2).Chunk(2).Where(a => a is ["--sql", _]).Select(a => Path.GetFullPath(a[1])).ToList();

// progetti dell'app d'esempio: cartella -> riferimenti e tipo di output
var layout = new (string Dir, string[] Deps, OutputKind Kind)[]
{
    ("Data", [], OutputKind.DynamicallyLinkedLibrary), ("Core", ["Data"], OutputKind.DynamicallyLinkedLibrary),
    ("Web", ["Core", "Data"], OutputKind.DynamicallyLinkedLibrary), ("Legacy", ["Data"], OutputKind.DynamicallyLinkedLibrary),
    ("Batch", ["Core", "Data"], OutputKind.ConsoleApplication),
};

var framework = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
    .Where(p => Path.GetFileName(p) is var f && (f.StartsWith("System.", StringComparison.Ordinal) || f is "mscorlib.dll" or "netstandard.dll" or "Microsoft.CSharp.dll"))
    .Where(p => !Path.GetFileName(p).StartsWith("System.Composition", StringComparison.Ordinal))
    .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToList();

var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Disable);
var stubTrees = Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "stubs"), "*.cs").Order()
    .Select(f => CSharpSyntaxTree.ParseText(File.ReadAllText(f), path: f));
using var stubImage = new MemoryStream();
var emitted = CSharpCompilation.Create("FrameworkStubs", stubTrees, framework, options).Emit(stubImage);
if (!emitted.Success)
{
    foreach (var d in emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)) Console.Error.WriteLine(d);
    return 1;
}
var references = framework.Append(MetadataReference.CreateFromImage(stubImage.ToArray())).ToList();

var solution = new AdhocWorkspace().CurrentSolution;
var ids = new Dictionary<string, ProjectId>();
foreach (var (dir, deps, kind) in layout)
{
    var folder = Path.Combine(appRoot, dir);
    if (!Directory.Exists(folder)) continue;
    var name = "Shop." + dir;
    var pid = ids[dir] = ProjectId.CreateNewId(name);
    solution = solution.AddProject(ProjectInfo.Create(pid, VersionStamp.Default, name, name, LanguageNames.CSharp,
        filePath: Path.Combine(folder, name + ".csproj"), compilationOptions: options.WithOutputKind(kind),
        parseOptions: new CSharpParseOptions(LanguageVersion.Latest, DocumentationMode.Parse),
        metadataReferences: references,
        projectReferences: deps.Where(ids.ContainsKey).Select(d => new ProjectReference(ids[d]))));
    foreach (var f in Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        solution = solution.AddDocument(DocumentId.CreateNewId(pid), Path.GetFileName(f), SourceText.From(File.ReadAllText(f)), filePath: f);
}

int errors = 0;
foreach (var p in solution.Projects)
    foreach (var d in (await p.GetCompilationAsync())!.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error))
    {
        Console.Error.WriteLine($"{p.Name}: {d}");
        errors++;
    }
if (errors > 0) { Console.Error.WriteLine($"{errors} errori di compilazione nell'app d'esempio"); return 1; }

var graph = new CodeGraph();
await new Extractor(graph, appRoot, new Options { Input = "Shop.sln", SqlPaths = sql, NoSql = sql.Count == 0 }).RunAsync(solution);
// i csproj dell'esempio sono virtuali (niente MSBuild): non ha senso indicarli come file
foreach (var n in graph.Nodes.Values.Where(n => n.Kind == "Project")) n.Props.Remove("file");
graph.Meta["tool"] = "roslyn-graph 0.3 (sample)";
graph.Meta["input"] = "Shop.sln";
// radice relativa: codegraph.py la risolve rispetto alla cartella di graph.json
graph.Meta["root"] = Path.GetRelativePath(Path.GetDirectoryName(outPath)!, appRoot).Replace('\\', '/');
graph.Meta["counts"] = graph.Nodes.Values.GroupBy(n => n.Kind).OrderBy(g => g.Key).ToDictionary(g => g.Key, g => g.Count());
graph.Meta["edgeCounts"] = graph.Edges.GroupBy(e => e.Type).OrderBy(g => g.Key).ToDictionary(g => g.Key, g => g.Count());
graph.WriteJson(outPath);
Console.Error.WriteLine($"{graph.Nodes.Count} nodi, {graph.EdgeCount} archi -> {outPath}");
return 0;
