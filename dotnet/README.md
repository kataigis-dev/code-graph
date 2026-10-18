# roslyn-graph-kit

Kit per costruire con Roslyn il grafo di un monolite ASP.NET su .NET Framework (MVC, Web API, Web Forms) e farlo usare
a un LLM per il reverse engineering: documentazione as-is, catalogo delle regole di business, bozze di specifica per SDD.

```
extractor/   console .NET (Roslyn) che legge la solution e scrive graph.json, una passata per file
             (Extractor.cs dichiarazioni e MVC, .Code.cs corpi dei metodi, .Razor.cs viste, .WebForms.cs Web Forms)
tools/       codegraph.py   caricamento del grafo (networkx), dossier delle rotte, sorgente e contesto per l'LLM
             graph_docs.py  documentazione Markdown navigabile: un dossier per entry point, schede di tipi, tabelle, viste, report
             enrich.py      arricchimento con LLM: riassunti dei metodi e bozze di specifica per rotta (facoltativo)
agent/       CLAUDE.md e comando /as-is per Claude Code (o istruzioni per un altro agente)
sample/      app d'esempio (MVC + Web Forms), SampleGraph (genera graph.sample.json senza Windows) e graph.sample.json
```

## Come l'LLM usa il grafo

Il principio e' il graph-RAG: l'LLM non riceve file interi, ma solo cio' che e' collegato alla domanda. Qui il grafo
diventa una cartella di Markdown che l'agente legge con i normali strumenti su file (lettura e ricerca testuale):

1. parte da `INDEX.md` e dal dossier di una rotta, che elenca metodi raggiunti, tabelle, viste, stato implicito, validazioni;
2. segue i link alle schede dei metodi e delle tabelle;
3. apre il sorgente solo ai file:riga indicati;
4. per sapere dove compare un nodo cerca il suo id (`M:...`, `DB:...`) in tutta la cartella.

Rispetto a un server MCP non c'e' un processo da avviare, funziona con qualsiasi agente, si versiona insieme ai documenti
as-is e le persone possono leggere la stessa cosa che legge l'agente.

Il flusso completo:

```
solution legacy --(extractor)--> graph.json --(enrich.py, facoltativo)--> summaries.jsonl
                                     \                                       /
                                      `----------(graph_docs.py)------------'--> docs/graph/*.md --> agente + CLAUDE.md
```

## 1. Generare il grafo

Prerequisiti, su Windows:

- Visual Studio 2022+ o Build Tools con il componente per lo sviluppo web: i csproj ASP.NET importano
  `Microsoft.WebApplication.targets` e senza di esso non si caricano (errore MSB4019).
- Il Developer Pack della versione di .NET Framework usata dai progetti (4.6.1, 4.7.2, 4.8, ...): senza, i riferimenti
  non si risolvono e il grafo perde archi.
- SDK .NET 10 per compilare ed eseguire l'estrattore.

```powershell
# nella cartella della solution legacy
nuget restore MiaApp.sln          # oppure: msbuild MiaApp.sln -t:restore

# nella cartella extractor
dotnet run -c Release -- C:\src\MiaApp\MiaApp.sln --out C:\analisi\graph.json --neo4j C:\analisi\neo4j
```

L'estrattore gira su .NET 10 ma analizza progetti .NET Framework: Roslyn carica i csproj "vecchio stile" in un processo
separato (`BuildHost-net472`) che usa l'MSBuild di Visual Studio. Per questo non serve, e non funziona, compilare
l'estrattore per `net48`.

Dopo il primo run controlla in `graph.json` `meta.workspaceFailures` e i `declarationErrors` di ogni progetto in
`meta.projects`: molti errori vogliono dire riferimenti non ripristinati, e le chiamate non risolte finiscono in
`unresolved_calls`. Dopo un aggiornamento dei pacchetti, controlla le dipendenze vulnerabili:

```powershell
dotnet list package --vulnerable --include-transitive
```

### Cosa contiene il grafo

| Nodo | Id | Attributi principali |
| --- | --- | --- |
| Project | `PRJ:Nome` | assembly, errori di dichiarazione |
| Type | `T:Ns.Tipo` (id di documentazione Roslyn) | fqn, file, baseType, interfaces, isController, isDbContext |
| Method | `M:Ns.Tipo.Metodo(Tipi)` | firma, file e righe, loc, complessita', integrazioni, chiamate esterne, stato di sessione, chiavi di config, validazioni, eccezioni, SQL |
| Route | `R:POST /Orders/Create` | controller o cartella, action, verbi, template, autorizzazioni, filtri, flavor (mvc, webapi, aspnetcore, convention se riconosciuto solo dal nome, webforms, asmx, ashx, global) |
| View | `V:percorso.cshtml` | modello, campi legati, chiavi ViewBag, ruoli controllati, URL ajax |
| Page | `P:percorso.aspx` (anche ascx, master, asmx, ashx, asax) | code-behind, controlli, campi legati, validatori, SQL delle origini dati, codice inline |
| Table | `DB:nome` | origine (EF o SQL), entita' mappata |
| StoredProcedure | `SP:nome` | |

Se lo stesso id compare in due progetti della solution (classi copiate con lo stesso namespace), il secondo nodo prende
il suffisso `@Assembly`, per esempio `M:Shop.Common.Util.F@Shop.Core`.

| Arco | Significato |
| --- | --- |
| CALLS | chiamata diretta (anche getter/setter con logica e costruttori) |
| DISPATCHES_TO | da metodo di interfaccia/virtuale all'implementazione: fa attraversare la DI |
| HANDLED_BY, EXPOSES | rotta -> action, metodo o pagina; controller o pagina -> rotta |
| RENDERS, REDIRECTS_TO | action -> vista; metodo o pagina -> rotta (RedirectToAction, Response.Redirect, Server.Transfer) |
| READS, WRITES, CALLS_PROC | metodo o pagina -> tabella / stored procedure; `via` = `ef`, `ef-tracking` (euristico) o `sql` (euristico) |
| HANDLES_EVENT | pagina -> gestore di evento nel code-behind (Page_Load, btnSave_Click, ...), con controllo ed evento |
| CODE_BEHIND, USES_CONTROL, USES_MASTER | pagina -> classe, controllo utente (.ascx), master page |
| USES_MODEL, LINKS_TO, POSTS_TO, RENDERS_PARTIAL, USES_LAYOUT, RENDERS_ACTION | dalle viste Razor e dalle pagine |
| INHERITS, IMPLEMENTS, CONTAINS, USES_TYPE, INSTANTIATES, MAPPED_TO, DECLARES_TABLE | struttura |

Le autorizzazioni di una rotta comprendono gli attributi ereditati da controller e metodi base
(`Authorize(Roles=Admin) [ereditato da BaseController]`). Le update di Entity Framework fatte per change tracking
(`entita.Campo = valore; SaveChanges()` oppure `Entry(x).State = Modified`) diventano archi WRITES con `via = ef-tracking`.

### Web Forms

Il markup di `.aspx`, `.ascx`, `.master`, `.asmx`, `.ashx` e `Global.asax` si legge con espressioni regolari, come le
viste Razor; il code-behind e' gia' nel grafo e viene collegato alla pagina tramite `Inherits`/`Class`.

- Entry point: una rotta per ogni pagina `.aspx` (GET e POST), per ogni `[WebMethod]` di un `.asmx`, per il
  `ProcessRequest` di un `.ashx` e per gli eventi `Application_*` e `Session_*` di `Global.asax`.
  Gli URL di `MapPageRoute` diventano `friendly_urls` della rotta della pagina.
- Gestori di evento: attributi `On*` dei controlli server (`OnClick="btnSave_Click"`), metodi `Page_*` (AutoEventWireup),
  override del ciclo di vita (`OnInit`, `OnLoad`, ...) e collegamenti nel codice (`btnDelete.Click += btnDelete_Click`).
- Controlli server con `ID`, campi legati (`BoundField`, `Eval`, `Bind`), validatori con la loro regola
  (`RangeValidator` 0..50000, `RegularExpressionValidator`, ...), ruoli dei `RoleGroup` e di `IsInRole`.
- Origini dati: `SqlDataSource` (SQL e stored procedure -> archi READS/WRITES/CALLS_PROC dalla pagina),
  `ObjectDataSource` (-> CALLS verso i metodi indicati), `EntityDataSource` e `LinqDataSource`.
- Navigazione: `NavigateUrl`, `PostBackUrl`, `href`, `Response.Redirect` e `Server.Transfer`, nel markup e nel code-behind.
- Controlli utente (`<%@ Register Src=... %>`), master page (`MasterPageFile`), stato nel markup (Session, ViewState, Request).
- Autorizzazioni: le regole `<authorization>` dei `web.config` (radice, sottocartelle, `<location path>`) vengono
  attribuite a ogni pagina e servizio, dalla piu' specifica alla piu' generale, nell'ordine in cui le valuta ASP.NET.

## 2. Generare la documentazione Markdown

```bash
pip install -r tools/requirements.txt          # Python 3.9 o successivo
python tools/graph_docs.py --graph C:\analisi\graph.json --out C:\src\MiaApp\docs\graph
```

Mettila dentro il repository legacy (`docs/graph`): i link ai sorgenti sono relativi e l'agente la trova accanto al codice.
`--root` serve solo se i sorgenti non sono dove indica `meta.root` del grafo; `--summaries` aggiunge i riassunti di `enrich.py`.

```
INDEX.md                      punto di partenza: numeri, progetti, entry point, dati, report
routes/INDEX.md               gruppi di entry point (controller, cartelle Web Forms, servizi)
routes/<gruppo>.md            rotte del gruppo, con autorizzazioni e tabelle scritte
routes/<gruppo>/<rotta>.md    dossier di una rotta: metodi raggiunti, dati, viste e pagine, redirect, integrazioni,
                              stato implicito, validazioni, eccezioni
types/INDEX.md                progetti
types/<progetto>/INDEX.md     namespace e tipi
types/<progetto>/<tipo>.md    metodi: firma, posizione, fatti estratti, dati, chiamati, chiamanti, rotte che li raggiungono
tables/INDEX.md               tabelle e stored procedure
tables/<tabella>.md           chi legge, chi scrive (con via e riga), da quali rotte
views/INDEX.md                viste Razor e file Web Forms per progetto
views/<progetto>.md           modello o code-behind, controlli, campi, validatori, gestori di evento, form e link
reports/authorization.md      autorizzazioni per gruppo e rotte senza autorizzazione dichiarata
reports/data-ownership.md     tabelle scritte da piu' gruppi di entry point: i confini da decidere per i microservizi
reports/implicit-state.md     Session, ViewState, TempData, ViewBag, Cache
reports/integrations.md       email, http, wcf, code, file system, sql
reports/hotspots.md           metodi piu' complessi e lunghi, logica nel markup
reports/gaps.md               chiamate, viste, link e gestori non risolti; avvisi di caricamento dei progetti
```

Pensata per progetti grandi:

- un file per rotta e un indice per ogni livello;
- le classi con piu' di 60 metodi divise in file da 40 (`<tipo>.2.md`, ...);
- elenchi lunghi troncati con "... e altri N";
- il codice non viene copiato, solo file e riga.

Su un grafo sintetico da 26.000 nodi e 106.000 archi produce circa 6.000 file in una ventina di secondi, con 320 MB di memoria.

La cartella viene rigenerata a ogni esecuzione: si cancellano solo `INDEX.md` e le sottocartelle elencate sopra, e solo
se la cartella e' vuota o contiene il file `.graph-docs` lasciato da un'esecuzione precedente (altrimenti serve `--force`).

## 3. Collegare l'agente

**Claude Code:** copia `agent/CLAUDE.md` nella radice del repository legacy e `agent/commands/as-is.md` in
`.claude/commands/`, poi: `/as-is Orders`. L'agente scrive in `docs/as-is/` i casi d'uso e il catalogo delle regole
(`docs/as-is/rules.md`).

**Altri agenti:** usa `agent/CLAUDE.md` come istruzioni di sistema. Servono solo lettura di file e ricerca testuale.

Prova con l'app d'esempio:

```bash
python tools/graph_docs.py --graph sample/graph.sample.json --out sample/docs-graph
```

`sample/graph.sample.json` si rigenera con l'estrattore vero, anche senza Windows: `SampleGraph` carica i progetti
d'esempio in memoria e sostituisce `System.Web` ed Entity Framework 6 con stub minimi (`sample/SampleGraph/stubs`).

```bash
dotnet run --project sample/SampleGraph -c Release -- sample/app sample/graph.sample.json
```

## 4. Arricchimento con LLM (facoltativo)

```bash
# stima a costo zero: nessuna chiamata LLM, salva i prompt per misurarne la dimensione
python tools/enrich.py --graph graph.json --provider fake --dump-prompts prompts --scope Orders

# esecuzione reale su una capability, con bozze di specifica per le rotte
set ANTHROPIC_API_KEY=...
python tools/enrich.py --graph graph.json --provider anthropic --summaries summaries.jsonl --scope Orders --routes --specs-dir specs-as-is

# poi rigenera la documentazione con i riassunti
python tools/graph_docs.py --graph graph.json --summaries summaries.jsonl --out C:\src\MiaApp\docs\graph
```

- `enrich.py` riassume i metodi in ordine topologico inverso: prima le foglie, poi chi le chiama, passando ogni volta
  i riassunti gia' fatti dei chiamati. Ogni prompt resta piccolo e l'informazione risale fino alle action.
  Poi genera per ogni rotta una bozza di specifica as-is.
- `--provider` e' obbligatorio: `anthropic` e `openai` inviano il sorgente al modello, `fake` non fa chiamate di rete.
- `--scope` limita il lavoro ai metodi raggiungibili da rotte o controller che contengono il testo dato.
- L'esecuzione e' incrementale: un metodo si rielabora solo se cambia il suo sorgente o i riassunti dei chiamati,
  anche in presenza di ricorsione mutua; una rotta solo se cambia il suo dossier. Un riassunto `fake` non vale come
  cache per un provider reale.
- `--max-methods N` per prove e stime di costo (`0` non elabora nulla).
- Gli errori non recuperabili (credenziali, richiesta non valida, risposta troncata da `--max-tokens`) non vengono ritentati.
- `--provider openai --base-url ...` per endpoint compatibili OpenAI (Azure OpenAI, modelli on-premise).
  Per Bedrock o Vertex basta sostituire il client in `AnthropicLLM` con `AnthropicBedrock` / `AnthropicVertex`.
- Metodi banali (accessor, deleghe di una riga) vengono riassunti senza LLM.

## 5. Neo4j (facoltativo)

Con `--neo4j` l'estrattore scrive `nodes.csv` ed `edges.csv`. Esempi di query dopo l'import:

```cypher
// tabelle scritte da piu' controller: candidate a conflitti di proprieta' tra servizi
MATCH (r:Route)-[:HANDLED_BY]->(:Method)-[:CALLS|DISPATCHES_TO*0..8]->(m:Method)-[:WRITES]->(t:Table)
WITH t, collect(DISTINCT split(r.id, '/')[1]) AS controllers
WHERE size(controllers) > 1 RETURN t.name, controllers ORDER BY size(controllers) DESC;

// pagine Web Forms che scrivono su una tabella, direttamente o dai gestori di evento
MATCH (p:Page)-[:HANDLES_EVENT|USES_CONTROL*0..2]->(x)-[:CALLS|DISPATCHES_TO*0..6]->()-[:WRITES]->(t:Table)
RETURN p.id, collect(DISTINCT t.name);
```

## Limiti noti

- Reflection, `dynamic`, risoluzione DI per nome o factory: le chiamate non risolte finiscono in `unresolved_calls`.
- Rotte convenzionali personalizzate in `RouteConfig` non vengono lette: si assume `{controller}/{action}`.
- Autorizzazione: non vengono letti i filtri globali (`FilterConfig`, `GlobalFilters`), i controlli nel codice
  (`User.IsInRole`, verifiche manuali) e i moduli HTTP. Le action pubbliche ereditate da un controller base non
  diventano rotte del controller derivato.
- Entity Framework: le update per change tracking sono dedotte (`ef-tracking`) solo su entita' mappate in un DbContext,
  senza verificare che poi venga chiamato `SaveChanges`; `Set<T>()` con `T` generico (repository generici) non dice
  la tabella e finisce in `unresolved_calls`.
- SQL dinamico costruito a pezzi: rilevamento parziale e marcato come euristico.
- JavaScript nelle viste: solo gli URL ajax letterali vengono rilevati.
- Web Forms: i progetti "web site" (`CodeFile`, `App_Code`, senza csproj) non sono supportati; il codice scritto
  direttamente nei file `.ashx`/`.asmx` o in `<script runat="server">` non e' compilato e non entra nel grafo
  (viene solo contato); non si risolvono i controlli registrati in `web.config` (`<pages><controls>`) e gli URL che
  includono la virtual directory.
- Logica nelle stored procedure: va estratta a parte (es. da `sys.sql_modules`) e aggiunta al catalogo regole.
- VB.NET non e' coperto.

## Sicurezza e riservatezza

- `enrich.py` invia codice sorgente al modello, e lo stesso fa l'agente quando legge i file. Usa un endpoint approvato
  dalla tua azienda e verifica le policy prima di analizzare codice proprietario.
- Prima dell'invio, `enrich.py` maschera con `***` i valori di password, chiavi e token che trova nelle stringhe del
  codice (connection string, assegnazioni a variabili come `apiKey`). E' una rete di sicurezza, non una garanzia:
  `--no-redact` la disattiva.
- L'estrattore carica i progetti con MSBuild, che esegue i target dei csproj: lancialo solo su repository fidati,
  come faresti per compilarli.
- Il contenuto del codice legacy (commenti, stringhe) arriva nel contesto dell'agente: `agent/CLAUDE.md` gli chiede di
  trattarlo come dato e di non eseguire istruzioni trovate nei sorgenti.
- `codegraph.py` legge solo file dentro la radice dei sorgenti, anche se `graph.json` indica percorsi esterni.
- `docs/graph` descrive struttura del codice, query SQL e nomi delle connection string (non i valori):
  trattala con la stessa riservatezza del sorgente.
