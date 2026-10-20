using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace RoslynGraph;

/// <summary>
/// Passata 0: schema del database dagli script .sql (SSMS "Genera script", progetto SSDT, SqlPackage /Action:Script).
/// Il T-SQL si legge con ScriptDom, il parser di SQL Server: niente euristiche, gli archi hanno via = "schema".
///
///   Table: colonne (tipo, null, default, identity, calcolate), chiave primaria, vincoli CHECK, UNIQUE, DEFAULT, FK
///   Table -REFERENCES-> Table (chiavi esterne), Table -TRIGGERS-> Trigger
///   StoredProcedure (procedure e funzioni), Trigger, viste (Table con dbObject = view):
///     -READS/-WRITES-> Table, -CALLS_PROC-> StoredProcedure, eccezioni (RAISERROR, THROW) con la condizione che le genera
///
/// Gli script vanno messi dentro la radice dei sorgenti: i link e la lettura delle righe funzionano solo li'.
/// </summary>
public sealed partial class Extractor
{
    private static readonly Regex GoLine = new(@"^\s*GO\s*(?:\d+\s*)?(?:--.*)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly HashSet<string> IgnoredSchemas = new(StringComparer.OrdinalIgnoreCase) { "sys", "information_schema" };
    private static readonly HashSet<string> SystemProcs = new(StringComparer.OrdinalIgnoreCase)
    {
        "sp_rename", "sp_addextendedproperty", "sp_updateextendedproperty", "sp_getapplock", "sp_releaseapplock",
        "sp_set_session_context", "sp_refreshview", "sp_recompile", "sp_xml_preparedocument", "sp_xml_removedocument",
    };
    private readonly List<string> _sqlErrors = new();

    private void PassSql()
    {
        if (opt.NoSql)
        {
            Warn("schema SQL non fornito (--no-sql): vincoli, trigger e logica delle stored procedure restano fuori dal grafo");
            _g.Meta["sqlSchema"] = null;
            return;
        }
        var files = new List<string>();
        foreach (var path in opt.SqlPaths.Select(Path.GetFullPath))
        {
            if (File.Exists(path)) files.Add(path);
            else if (Directory.Exists(path))
                files.AddRange(Directory.EnumerateFiles(path, "*.sql", SearchOption.AllDirectories)
                    .Where(f => !Paths.IsExcluded(f, path)).Order(StringComparer.Ordinal));
            else Warn($"schema SQL non trovato: {path}");
        }
        foreach (var f in files.Where(f => Rel(f).StartsWith("..", StringComparison.Ordinal)).Take(1))
            Warn($"{f} e' fuori dalla radice dei sorgenti: link e righe non saranno leggibili. Copia lo schema nel repository.");

        var counts = new Dictionary<string, int>();
        foreach (var file in files)
        {
            string text;
            try { text = File.ReadAllText(file); }   // riconosce il BOM: gli script di SSMS sono spesso UTF-16
            catch (Exception ex) { Warn($"script SQL non leggibile {Rel(file)}: {ex.Message}"); continue; }
            foreach (var (batch, offset) in Batches(text))
                foreach (var kind in ParseBatch(Rel(file), batch, offset))
                    counts[kind] = counts.GetValueOrDefault(kind) + 1;
        }
        _g.Meta["sqlSchema"] = new { files = files.Select(Rel).Take(200).ToList(), fileCount = files.Count, objects = counts, parseErrors = _sqlErrors };
        Info($"   schema SQL: {files.Count} file, " + (counts.Count == 0 ? "nessun oggetto" : string.Join(", ", counts.Select(kv => $"{kv.Value} {kv.Key}")))
             + (_sqlErrors.Count > 0 ? $", {_sqlErrors.Count} blocchi non interpretati (vedi meta.sqlSchema.parseErrors)" : ""));
    }

    /// <summary>Blocchi separati da GO, con la riga di partenza: un errore di sintassi resta confinato al suo blocco.</summary>
    private static IEnumerable<(string Text, int Offset)> Batches(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var current = new List<string>();
        int start = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            if (!GoLine.IsMatch(lines[i])) { current.Add(lines[i]); continue; }
            if (current.Any(l => l.Trim().Length > 0)) yield return (string.Join("\n", current), start);
            current.Clear();
            start = i + 1;
        }
        if (current.Any(l => l.Trim().Length > 0)) yield return (string.Join("\n", current), start);
    }

    private IEnumerable<string> ParseBatch(string file, string batch, int offset)
    {
        var parser = new TSql180Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(batch);
        var fragment = parser.Parse(reader, out var errors);
        if (errors.Count > 0)
        {
            var e = errors[0];
            if (_sqlErrors.Count < 100) _sqlErrors.Add($"{file}:{e.Line + offset}: {e.Message}");
            yield break;
        }
        foreach (var stmt in (fragment as TSqlScript)?.Batches.SelectMany(b => b.Statements) ?? [])
        {
            var kind = stmt switch
            {
                CreateTableStatement ct => SqlTable(file, offset, ct),
                AlterTableAddTableElementStatement at => SqlAlterTable(file, offset, at),
                CreateIndexStatement { Unique: true } ci => SqlUniqueIndex(file, offset, ci),
                ViewStatementBody v => SqlView(file, offset, v),
                ProcedureStatementBody p => SqlRoutine(file, offset, p, p.ProcedureReference.Name, "procedure"),
                FunctionStatementBody f => SqlRoutine(file, offset, f, f.Name, "function"),
                TriggerStatementBody t => SqlTrigger(file, offset, t),
                _ => null,
            };
            if (kind is not null) yield return kind;
        }
    }

    // ------------------------------------------------------------------ tabelle e vincoli

    private string? SqlTable(string file, int offset, CreateTableStatement ct)
    {
        if (ct.SchemaObjectName.BaseIdentifier.Value.StartsWith('#') || ct.Definition is null) return null;
        var n = SchemaObject("Table", ct.SchemaObjectName, file, offset, ct, "table");
        if (n is null) return null;
        AddTableElements(n, file, offset, ct.Definition);
        return "tabelle";
    }

    private string? SqlAlterTable(string file, int offset, AlterTableAddTableElementStatement at)
    {
        if (at.Definition is null) return null;
        var n = _g.Node(SqlId("Table", at.SchemaObjectName), "Table", Display(at.SchemaObjectName));
        AddTableElements(n, file, offset, at.Definition);
        return null;
    }

    private string? SqlUniqueIndex(string file, int offset, CreateIndexStatement ci)
    {
        var n = _g.Node(SqlId("Table", ci.OnName), "Table", Display(ci.OnName));
        n.AddItem("constraints", new()
        {
            ["kind"] = "UNIQUE", ["name"] = ci.Name?.Value, ["columns"] = ci.Columns.Select(c => ColumnName(c.Column)).ToList(),
            ["filter"] = ci.FilterPredicate is null ? null : SqlText(ci.FilterPredicate), ["file"] = file, ["line"] = ci.StartLine + offset,
        }, 300);
        return null;
    }

    private void AddTableElements(GraphNode table, string file, int offset, TableDefinition def)
    {
        foreach (var col in def.ColumnDefinitions)
        {
            var name = col.ColumnIdentifier.Value;
            var nullable = col.Constraints.OfType<NullableConstraintDefinition>().FirstOrDefault()?.Nullable;
            table.AddItem("columns", new()
            {
                ["name"] = name, ["type"] = col.DataType is null ? null : TypeText(col.DataType),
                // senza NULL / NOT NULL esplicito vale l'impostazione del database (di solito NULL); identity e PK sono NOT NULL
                ["nullable"] = nullable ?? (col.IdentityOptions is null && !col.Constraints.OfType<UniqueConstraintDefinition>().Any(u => u.IsPrimaryKey)),
                ["default"] = col.DefaultConstraint?.Expression is { } d ? SqlText(d) : null,
                ["identity"] = col.IdentityOptions is null ? null : true,
                ["computed"] = col.ComputedColumnExpression is { } c ? SqlText(c) : null,
                ["line"] = col.StartLine + offset,
            }, 300);
            foreach (var cons in col.Constraints.Where(x => x is not NullableConstraintDefinition))
                AddConstraint(table, file, offset, cons, name);
        }
        foreach (var cons in def.TableConstraints) AddConstraint(table, file, offset, cons, null);
    }

    private void AddConstraint(GraphNode table, string file, int offset, ConstraintDefinition cons, string? column)
    {
        var item = new Dictionary<string, object?> { ["name"] = cons.ConstraintIdentifier?.Value, ["file"] = file, ["line"] = cons.StartLine + offset };
        switch (cons)
        {
            case CheckConstraintDefinition ck:
                item["kind"] = "CHECK";
                item["expression"] = SqlText(ck.CheckCondition);
                var refs = new ColumnRefs();
                ck.CheckCondition.Accept(refs);
                item["columns"] = column is not null ? new List<string> { column } : refs.Names.ToList();
                break;
            case UniqueConstraintDefinition uq:
                var cols = uq.Columns.Count > 0 ? uq.Columns.Select(c => ColumnName(c.Column)).ToList() : [column ?? "?"];
                item["kind"] = uq.IsPrimaryKey ? "PRIMARY KEY" : "UNIQUE";
                item["columns"] = cols;
                if (uq.IsPrimaryKey) table["primaryKey"] = cols;
                break;
            case DefaultConstraintDefinition df:
                item["kind"] = "DEFAULT";
                item["columns"] = new List<string> { df.Column?.Value ?? column ?? "?" };
                item["expression"] = SqlText(df.Expression);
                break;
            case ForeignKeyConstraintDefinition fk:
                var from = fk.Columns.Count > 0 ? fk.Columns.Select(c => c.Value).ToList() : [column ?? "?"];
                var to = fk.ReferencedTableColumns.Select(c => c.Value).ToList();
                var target = _g.Node(SqlId("Table", fk.ReferenceTableName), "Table", Display(fk.ReferenceTableName));
                item["kind"] = "FOREIGN KEY";
                item["columns"] = from;
                item["references"] = $"{target.Name}({string.Join(", ", to)})";
                if (fk.DeleteAction is not DeleteUpdateAction.NotSpecified and not DeleteUpdateAction.NoAction)
                    item["onDelete"] = fk.DeleteAction.ToString();
                var e = _g.Edge(table.Id, target.Id, "REFERENCES", cons.StartLine + offset);
                e["columns"] = from;
                e["refColumns"] = to;
                e["via"] = "schema";
                break;
            default:
                return;
        }
        table.AddItem("constraints", item, 300);
    }

    private string? SqlView(string file, int offset, ViewStatementBody v)
    {
        var n = SchemaObject("Table", v.SchemaObjectName, file, offset, v, "view");
        if (n is null) return null;
        AnalyzeSqlBody(n, file, offset, v.SelectStatement);
        return "viste";
    }

    // ------------------------------------------------------------------ procedure, funzioni, trigger

    private string? SqlRoutine(string file, int offset, ProcedureStatementBodyBase r, SchemaObjectName name, string dbObject)
    {
        var n = SchemaObject("StoredProcedure", name, file, offset, r, dbObject);
        if (n is null) return null;
        n["parameters"] = r.Parameters.Select(p => $"{p.VariableName.Value} {TypeText(p.DataType)}"
                                                    + (p.Value is null ? "" : $" = {SqlText(p.Value)}")
                                                    + (p.Modifier == ParameterModifier.Output ? " OUTPUT" : "")).ToList();
        if (r.StatementList is not null) AnalyzeSqlBody(n, file, offset, r.StatementList);
        // funzioni inline: RETURNS TABLE AS RETURN (SELECT ...)
        if (r is FunctionStatementBody { ReturnType: SelectFunctionReturnType sel }) AnalyzeSqlBody(n, file, offset, sel.SelectStatement);
        return dbObject == "procedure" ? "stored procedure" : "funzioni";
    }

    private string? SqlTrigger(string file, int offset, TriggerStatementBody t)
    {
        if (t.TriggerObject?.TriggerScope != TriggerScope.Normal) return null;   // trigger DDL o di logon: non riguardano i dati
        var n = SchemaObject("Trigger", t.Name, file, offset, t, "trigger");
        if (n is null) return null;
        var table = _g.Node(SqlId("Table", t.TriggerObject.Name), "Table", Display(t.TriggerObject.Name));
        n["table"] = table.Name;
        n["timing"] = t.TriggerType switch { TriggerType.InsteadOf => "INSTEAD OF", _ => "AFTER" };
        n["events"] = t.TriggerActions.Select(a => a.TriggerActionType.ToString().ToUpperInvariant()).ToList();
        _g.Edge(table.Id, n.Id, "TRIGGERS", t.StartLine + offset)["via"] = "schema";
        if (t.StatementList is not null) AnalyzeSqlBody(n, file, offset, t.StatementList, trigger: true);
        return "trigger";
    }

    /// <summary>Letture, scritture, chiamate, eccezioni e complessita' del corpo T-SQL di un oggetto.</summary>
    private void AnalyzeSqlBody(GraphNode n, string file, int offset, TSqlFragment body, bool trigger = false)
    {
        var ctes = new CteCollector();
        body.Accept(ctes);
        var v = new SqlBodyVisitor(ctes.Names, trigger);
        body.Accept(v);

        void Link(SchemaObjectName name, string kind, string edgeType, int line)
        {
            if (name.SchemaIdentifier?.Value is { } s && IgnoredSchemas.Contains(s)) return;
            var target = _g.Node(SqlId(kind, name), kind, Display(name));
            _g.Edge(n.Id, target.Id, edgeType, line + offset)["via"] = "schema";
        }
        foreach (var (name, line) in v.Writes) Link(name, "Table", "WRITES", line);
        foreach (var (name, line) in v.Reads) Link(name, "Table", "READS", line);
        foreach (var (name, line) in v.Calls.Where(c => !SystemProcs.Contains(c.Name.BaseIdentifier.Value)))
        {
            if (name.BaseIdentifier.Value.Equals("sp_send_dbmail", StringComparison.OrdinalIgnoreCase)) n.AddDistinct("integrations", "email");
            else Link(name, "StoredProcedure", "CALLS_PROC", line);
        }
        foreach (var t in v.Throws)
            n.AddItem("throws", new() { ["type"] = t.Kind, ["message"] = t.Message, ["condition"] = t.Condition, ["line"] = t.Line + offset }, 50);
        n["complexity"] = v.Complexity;
        n.Flag("dynamic_sql", v.Dynamic);
    }

    // ------------------------------------------------------------------ helper

    /// <summary>Nodo di un oggetto dello schema con la sua posizione; null se lo stesso oggetto e' gia' definito altrove.</summary>
    private GraphNode? SchemaObject(string kind, SchemaObjectName name, string file, int offset, TSqlFragment stmt, string dbObject)
    {
        var n = _g.Node(SqlId(kind, name), kind, Display(name));
        if (n["inSchema"] is true)
        {
            Warn($"{Display(name)} definito piu' volte: tengo {n["file"]}:{n["startLine"]}, ignoro {file}:{stmt.StartLine + offset}");
            return null;
        }
        n["inSchema"] = true;
        n["dbObject"] = dbObject;
        n["schemaName"] = string.Join('.', new[] { name.SchemaIdentifier?.Value, name.BaseIdentifier.Value }.Where(x => x is not null));
        n["file"] = file;
        n["startLine"] = stmt.StartLine + offset;
        n["endLine"] = stmt.ScriptTokenStream[stmt.LastTokenIndex].Line + offset;
        n["loc"] = (int)n["endLine"]! - (int)n["startLine"]! + 1;
        return n;
    }

    private static string SqlId(string kind, SchemaObjectName name)
    {
        var raw = name.SchemaIdentifier is null ? name.BaseIdentifier.Value : $"{name.SchemaIdentifier.Value}.{name.BaseIdentifier.Value}";
        return kind switch { "Table" => "DB:", "Trigger" => "TR:", _ => "SP:" } + SqlHeuristics.NormalizeName(raw);
    }

    /// <summary>Nome leggibile: lo schema dbo si omette, come negli id.</summary>
    private static string Display(SchemaObjectName name) =>
        name.SchemaIdentifier is { Value: var s } && !s.Equals("dbo", StringComparison.OrdinalIgnoreCase)
            ? $"{s}.{name.BaseIdentifier.Value}" : name.BaseIdentifier.Value;

    /// <summary>nvarchar(100), decimal(18, 2), nvarchar(max): il frammento di un tipo tra [] non include sempre i parametri.</summary>
    private static string TypeText(DataTypeReference t)
    {
        var name = t.Name?.BaseIdentifier?.Value ?? SqlText(t);
        if (t.Name?.SchemaIdentifier is { Value: var s } && !s.Equals("sys", StringComparison.OrdinalIgnoreCase)) name = $"{s}.{name}";
        return t is ParameterizedDataTypeReference { Parameters.Count: > 0 } p
            ? $"{name}({string.Join(", ", p.Parameters.Select(x => x is MaxLiteral ? "max" : x.Value))})" : name;
    }

    private static string ColumnName(ColumnReferenceExpression? c) => c?.MultiPartIdentifier?.Identifiers.LastOrDefault()?.Value ?? "?";

    /// <summary>Testo originale del frammento, su una riga.</summary>
    internal static string SqlText(TSqlFragment f) => f.ScriptTokenStream is null || f.FirstTokenIndex < 0 ? "" : Text.OneLine(
        string.Concat(Enumerable.Range(f.FirstTokenIndex, f.LastTokenIndex - f.FirstTokenIndex + 1).Select(i => f.ScriptTokenStream[i].Text)), 300);

    private sealed class ColumnRefs : TSqlFragmentVisitor
    {
        public SortedSet<string> Names { get; } = new(StringComparer.OrdinalIgnoreCase);
        public override void Visit(ColumnReferenceExpression node) => Names.Add(ColumnName(node));
    }

    private sealed class CteCollector : TSqlFragmentVisitor
    {
        public HashSet<string> Names { get; } = new(StringComparer.OrdinalIgnoreCase);
        public override void Visit(CommonTableExpression node) => Names.Add(node.ExpressionName.Value);
    }

    /// <summary>
    /// Visita del corpo T-SQL. Visit(nodo) viene chiamato prima dei figli: i target di INSERT/UPDATE/DELETE/MERGE
    /// si registrano li', cosi' la visita della loro NamedTableReference non li conta anche come letture.
    /// </summary>
    private sealed class SqlBodyVisitor(HashSet<string> ctes, bool trigger) : TSqlFragmentVisitor
    {
        private readonly HashSet<TSqlFragment> _targets = new(ReferenceEqualityComparer.Instance);
        private readonly Stack<string> _conditions = new();
        public List<(SchemaObjectName Name, int Line)> Reads { get; } = new();
        public List<(SchemaObjectName Name, int Line)> Writes { get; } = new();
        public List<(SchemaObjectName Name, int Line)> Calls { get; } = new();
        public List<(string Kind, string? Message, string? Condition, int Line)> Throws { get; } = new();
        public int Complexity { get; private set; } = 1;
        public bool Dynamic { get; private set; }

        public override void Visit(InsertSpecification node) => Target(node.Target, null);
        public override void Visit(UpdateSpecification node) => Target(node.Target, node.FromClause);
        public override void Visit(DeleteSpecification node) => Target(node.Target, node.FromClause);
        public override void Visit(MergeSpecification node) => Target(node.Target, null);

        /// <summary>UPDATE o SET ... FROM dbo.Orders o: il target "o" e' l'alias di una tabella del FROM.</summary>
        private void Target(TableReference? target, FromClause? from)
        {
            if (target is not NamedTableReference t) return;
            _targets.Add(t);
            var name = t.SchemaObject;
            if (name.SchemaIdentifier is null && from is not null)
            {
                var aliased = new NamedTables();
                from.Accept(aliased);
                var match = aliased.Items.FirstOrDefault(x => x.Alias?.Value.Equals(name.BaseIdentifier.Value, StringComparison.OrdinalIgnoreCase) == true);
                if (match is not null) { _targets.Add(match); name = match.SchemaObject; }
            }
            if (Real(name)) Writes.Add((name, t.StartLine));
        }

        public override void Visit(NamedTableReference node)
        {
            if (!_targets.Contains(node) && Real(node.SchemaObject)) Reads.Add((node.SchemaObject, node.StartLine));
        }

        public override void Visit(ExecutableProcedureReference node)
        {
            if (node.ProcedureReference?.ProcedureReference?.Name is not { } name) { Dynamic = true; return; }  // EXEC @proc
            if (name.BaseIdentifier.Value.Equals("sp_executesql", StringComparison.OrdinalIgnoreCase)) Dynamic = true;
            else Calls.Add((name, node.StartLine));
        }

        public override void Visit(ExecutableStringList node) => Dynamic = true;   // EXEC('...') / EXEC(@sql)

        public override void Visit(FunctionCall node)
        {
            // le funzioni utente si chiamano sempre con lo schema (dbo.fn_x); quelle di sistema no
            if (node.CallTarget is MultiPartIdentifierCallTarget { MultiPartIdentifier.Identifiers: [.., var schema] })
                Calls.Add((new SchemaObjectName { Identifiers = { schema, node.FunctionName } }, node.StartLine));
        }

        public override void Visit(SchemaObjectFunctionTableReference node) => Calls.Add((node.SchemaObject, node.StartLine));

        public override void Visit(RaiseErrorStatement node) =>
            Throws.Add(("RAISERROR", (node.FirstParameter as StringLiteral)?.Value ?? SqlText(node.FirstParameter), Condition(), node.StartLine));

        public override void Visit(ThrowStatement node) =>
            Throws.Add(("THROW", node.ErrorNumber is null ? "(rilancio dell'errore)" : (node.Message as StringLiteral)?.Value
                                 ?? (node.Message is null ? null : SqlText(node.Message)), Condition(), node.StartLine));

        /// <summary>IF: la condizione vale nel ramo THEN, la sua negazione nell'ELSE.</summary>
        public override void ExplicitVisit(IfStatement node)
        {
            Complexity++;
            node.Predicate?.Accept(this);
            // testo tra IF e il ramo THEN: il frammento del predicato non include sempre EXISTS o NOT
            var cond = node.ThenStatement is { FirstTokenIndex: > 0 } then && node.ScriptTokenStream is { } tokens
                ? Text.OneLine(string.Concat(Enumerable.Range(node.FirstTokenIndex + 1, then.FirstTokenIndex - node.FirstTokenIndex - 1)
                                                       .Select(i => tokens[i].Text)), 300)
                : node.Predicate is null ? "?" : SqlText(node.Predicate);
            _conditions.Push(cond);
            node.ThenStatement?.Accept(this);
            _conditions.Pop();
            if (node.ElseStatement is null) return;
            _conditions.Push($"NOT ({cond})");
            node.ElseStatement.Accept(this);
            _conditions.Pop();
        }

        public override void Visit(WhileStatement node) => Complexity++;
        public override void Visit(SearchedCaseExpression node) => Complexity += Math.Max(1, node.WhenClauses.Count);
        public override void Visit(SimpleCaseExpression node) => Complexity += Math.Max(1, node.WhenClauses.Count);
        public override void Visit(TryCatchStatement node) => Complexity++;

        private string? Condition() => _conditions.Count == 0 ? null : string.Join(" AND ", _conditions.Reverse());

        /// <summary>Tabelle vere: niente temporanee, CTE e, nei trigger, le pseudo-tabelle inserted e deleted.</summary>
        private bool Real(SchemaObjectName name)
        {
            var b = name.BaseIdentifier?.Value;
            if (string.IsNullOrEmpty(b) || b.StartsWith('#') || b.StartsWith('@')) return false;
            if (name.SchemaIdentifier is null && ctes.Contains(b)) return false;
            return !(trigger && name.SchemaIdentifier is null && b.ToLowerInvariant() is "inserted" or "deleted");
        }

        private sealed class NamedTables : TSqlFragmentVisitor
        {
            public List<NamedTableReference> Items { get; } = new();
            public override void Visit(NamedTableReference node) => Items.Add(node);
        }
    }
}
