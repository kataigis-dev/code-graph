using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace RoslynGraph;

/// <summary>
/// Passata 7: valori di configurazione. Le chiavi &lt;appSettings&gt; di web.config, app.config e delle trasformazioni
/// (Web.Release.config, App.Debug.config, ...) diventano nodi Config con i valori per file; i metodi che le leggono
/// hanno un arco USES_CONFIG. I valori di produzione spesso non sono nel repository: la documentazione lo segnala.
/// Le connection string restano solo come nomi; i valori di chiavi che sembrano segreti vengono mascherati.
/// </summary>
public sealed partial class Extractor
{
    private static readonly Regex ConfigFile = new(@"^(web|app)(\.[\w-]+)?\.config$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SecretKey = new(@"password|passwd|pwd|secret|token|api[_-]?key|access[_-]?key|credential|private[_-]?key",
                                                  RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SecretValue = new(@"(password|pwd)\s*=", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private const string XdtNamespace = "http://schemas.microsoft.com/XML-Document-Transform";

    private readonly List<(string Method, string Key, int Line)> _pendingConfig = new();

    private void PassConfig()
    {
        int files = 0;
        foreach (var (p, _) in _units)
            foreach (var (full, projRel) in ProjectFiles(p, f => ConfigFile.IsMatch(Path.GetFileName(f))))
            {
                XDocument doc;
                try { doc = XDocument.Load(full, LoadOptions.SetLineInfo); }
                catch (Exception ex) { Warn($"file di configurazione non leggibile {Rel(full)}: {ex.Message}"); continue; }
                files++;
                var name = Path.GetFileName(full);
                // Web.Release.config -> ambiente Release; web.config e app.config -> base
                var env = Regex.Match(name, @"^(?:web|app)\.(?<e>[\w-]+)\.config$", RegexOptions.IgnoreCase) is { Success: true } m ? m.Groups["e"].Value : null;
                foreach (var settings in doc.Descendants().Where(e => e.Name.LocalName == "appSettings"))
                {
                    if (((string?)settings.Attribute("file") ?? (string?)settings.Attribute("configSource")) is { } external)
                        Warn($"{Rel(full)}: appSettings anche in {external} (letto solo se e' un *.config del progetto)");
                    foreach (var add in settings.Elements().Where(e => e.Name.LocalName == "add"))
                    {
                        if ((string?)add.Attribute("key") is not { Length: > 0 } key) continue;
                        var value = (string?)add.Attribute("value");
                        bool secret = SecretKey.IsMatch(key) || (value is not null && SecretValue.IsMatch(value));
                        var n = _g.Node("CFG:" + key, "Config", key);
                        n.Flag("secret", secret);
                        n.AddItem("values", new()
                        {
                            ["file"] = Rel(full), ["project"] = p.Name, ["environment"] = env,
                            ["value"] = secret && !string.IsNullOrEmpty(value) ? "***" : value,
                            ["transform"] = (string?)add.Attribute(XName.Get("Transform", XdtNamespace)),
                            ["line"] = ((IXmlLineInfo)add).HasLineInfo() ? ((IXmlLineInfo)add).LineNumber : null,
                        }, 50);
                    }
                }
            }

        foreach (var (method, key, line) in _pendingConfig)
        {
            var n = _g.Node("CFG:" + key, "Config", key);
            if (n["values"] is null) n["missing"] = true;   // letta nel codice, assente dai file di configurazione del repository
            _g.Edge(method, n.Id, "USES_CONFIG", line);
        }
        Info($"   configurazione: {files} file, {_g.Nodes.Values.Count(n => n.Kind == "Config")} chiavi");
    }
}
