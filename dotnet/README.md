# roslyn-graph-kit

Kit per costruire con Roslyn il grafo di un monolite ASP.NET su .NET Framework (MVC, Web API, Web Forms, WCF, batch) e del
suo schema SQL, e farlo usare a un LLM per il reverse engineering: documentazione as-is, catalogo delle regole di
business verificato, test di caratterizzazione da rieseguire sul nuovo sistema.

```
extractor/   console .NET (Roslyn + ScriptDom) che legge solution e schema SQL e scrive graph.json, una passata per file
             (.Sql.cs schema, Extractor.cs dichiarazioni e MVC, .Code.cs corpi dei metodi, .Razor.cs viste,
             .WebForms.cs Web Forms, .EntryPoints.cs servizi/batch/job/messaggi, .Config.cs configurazione)
tools/       codegraph.py   caricamento del grafo (networkx), dossier delle rotte, sorgente e contesto per l'LLM
             graph_docs.py  documentazione Markdown navigabile: dossier, schede di tipi e tabelle, report
             enrich.py      arricchimento con LLM: riassunti dei metodi e bozze di specifica per rotta (facoltativo)
             check_asis.py  controlli deterministici sui deliverable as-is (evidenze, valori, copertura, test)
             verify_rules.py verifica indipendente delle regole con un LLM (facoltativo)
             asis.py        lettura di catalogo, casi d'uso e scenari, usata dai due strumenti precedenti
agent/       istruzioni (AGENTS.md), skill as-is, as-is-tests e capability-map, prompt per Visual Studio 2022
sample/      app d'esempio (MVC, Web API, Web Forms, WCF, batch) con schema SQL, SampleGraph (genera graph.sample.json
             senza Windows), graph.sample.json, esempio di deliverable (docs/as-is) e di scenari (tests/as-is)
```

## Come l'LLM usa il grafo

Il principio e' il graph-RAG: l'LLM non riceve file interi, ma solo cio' che e' collegato alla domanda. Qui il grafo
diventa una cartella di Markdown che l'agente legge con i normali strumenti su file (lettura e ricerca testuale):

1. parte da `INDEX.md` e dal dossier di un entry point, che elenca metodi raggiunti, logica nel database, tabelle e
   vincoli, viste, stato implicito, configurazione, validazioni, eccezioni e punto di aggancio per i test;
2. segue i link alle schede dei metodi, delle tabelle, delle procedure e dei trigger;
3. apre il sorgente o lo script SQL solo ai file:riga indicati;
4. per sapere dove compare un nodo cerca il suo id (`M:...`, `DB:...`, `TR:...`) in tutta la cartella.

Rispetto a un server MCP non c'e' un processo da avviare, funziona con qualsiasi agente, si versiona insieme ai documenti
as-is e le persone possono leggere la stessa cosa che legge l'agente.

Il flusso completo:

```
solution legacy + schema SQL --(extractor)--> graph.json --(enrich.py, facoltativo)--> summaries.jsonl
                                                  \                                       /
                                                   `----------(graph_docs.py)------------'--> docs/graph/*.md
docs/graph + AGENTS.md + skill --(agente)--> docs/as-is (casi d'uso, regole, capability map, glossario)
                                             tests/as-is (scenari Gherkin + binding per il legacy)
docs/as-is --(check_asis.py, verify_rules.py, test sul legacy, esperti)--> docs/as-is/verifica + colonne Verifica e Revisione
```

## 1. Generare il grafo

Servono sempre due input: la solution e lo schema SQL del database. Senza schema mancano vincoli, trigger e la logica
delle stored procedure, che in un monolite .NET contengono molte regole.

Prerequisiti, su Windows:

- Visual Studio 2022+ o Build Tools con il componente per lo sviluppo web: i csproj ASP.NET importano
  `Microsoft.WebApplication.targets` e senza di esso non si caricano (errore MSB4019).
- Il Developer Pack della versione di .NET Framework usata dai progetti (4.6.1, 4.7.2, 4.8, ...): senza, i riferimenti
  non si risolvono e il grafo perde archi.
- SDK .NET 10 per compilare ed eseguire l'estrattore.

Lo schema SQL si fornisce come script `.sql`, dentro il repository legacy (altrimenti link e righe non sono leggibili):

- un progetto SSDT (`.sqlproj`) esistente: basta la sua cartella;
- da un database: SSMS, *Attivita' > Genera script* (solo schema), oppure
  `sqlpackage /Action:Extract /SourceConnectionString:"..." /TargetFile:Database /p:ExtractTarget=SchemaObjectType`,
  che scrive un file per oggetto in cartelle per schema e tipo.

```powershell
# nella cartella della solution legacy
nuget restore MiaApp.sln          # oppure: msbuild MiaApp.sln -t:restore

# nella cartella extractor
dotnet run -c Release -- C:\src\MiaApp\MiaApp.sln --sql C:\src\MiaApp\Database --out C:\analisi\graph.json --neo4j C:\analisi\neo4j
```

`--sql` e' obbligatorio (file o cartella, ripetibile); `--no-sql` procede senza schema e lo segnala in tutti i report.
Gli script si leggono con ScriptDom, il parser T-SQL di SQL Server, un blocco `GO` alla volta: un blocco con errori di
sintassi viene saltato e riportato in `meta.sqlSchema.parseErrors`.

L'estrattore gira su .NET 10 ma analizza progetti .NET Framework: Roslyn carica i csproj "vecchio stile" in un processo
separato (`BuildHost-net472`) che usa l'MSBuild di Visual Studio. Per questo non serve, e non funziona, compilare
l'estrattore per `net48`.

Dopo il primo run controlla in `graph.json` `meta.workspaceFailures`, i `declarationErrors` di ogni progetto in
`meta.projects` e `meta.sqlSchema`: molti errori vogliono dire riferimenti non ripristinati, e le chiamate non risolte
finiscono in `unresolved_calls`. Dopo un aggiornamento dei pacchetti, controlla le dipendenze vulnerabili:

```powershell
dotnet list package --vulnerable --include-transitive
```

### Cosa contiene il grafo

| Nodo | Id | Attributi principali |
| --- | --- | --- |
| Project | `PRJ:Nome` | assembly, errori di dichiarazione |
| Type | `T:Ns.Tipo` (id di documentazione Roslyn) | fqn, file, baseType, interfaces, isController, isDbContext, proprieta' con gli attributi ([Required], [Range], [Column], ...) |
| Method | `M:Ns.Tipo.Metodo(Tipi)` | firma, file e righe, loc, complessita', integrazioni, chiamate esterne, stato di sessione, chiavi di config, validazioni, eccezioni, SQL, messaggi inviati |
| Route | `R:POST /Orders/Create`, `R:JOB nome` | controller o cartella, action, verbi, template, autorizzazioni, filtri, flavor, pianificazione, messaggio |
| View | `V:percorso.cshtml` | modello, campi legati, chiavi ViewBag, ruoli controllati, URL ajax |
| Page | `P:percorso.aspx` (anche ascx, master, asmx, ashx, asax, svc) | code-behind, controlli, campi legati, validatori, SQL delle origini dati, codice inline |
| Table | `DB:nome` (anche le viste) | colonne (tipo, null, default, identity, calcolate), chiave primaria, vincoli CHECK/UNIQUE/DEFAULT/FK, file e righe dello script, origine nel codice (EF o SQL), entita' mappata |
| StoredProcedure | `SP:nome` (anche le funzioni) | parametri, file e righe, eccezioni (RAISERROR, THROW) con la condizione, complessita', SQL dinamico |
| Trigger | `TR:nome` | tabella, AFTER/INSTEAD OF, eventi, eccezioni con la condizione |
| Config | `CFG:chiave` | valori per file e ambiente (web.config, app.config, Web.Release.config, ...), segreti mascherati |

Flavor delle rotte: `mvc`, `webapi`, `aspnetcore`, `convention` (controller riconosciuto solo dal nome), `webforms`,
`asmx`, `ashx`, `global`, `wcf` (operazioni `[OperationContract]`, con l'URL se ospitate da un `.svc`), `service`
(Windows Service), `batch` e `desktop` (Main dei progetti eseguibili), `job` (Quartz `IJob`, Hangfire
`BackgroundJob`/`RecurringJob`), `timer`, `message` (handler NServiceBus, MassTransit, Rebus), `httpmodule`, `httphandler`
(registrati nel web.config), `signalr`, `owin`.

Se lo stesso id compare in due progetti della solution (classi copiate con lo stesso namespace), il secondo nodo prende
il suffisso `@Assembly`, per esempio `M:Shop.Common.Util.F@Shop.Core`.

| Arco | Significato |
| --- | --- |
| CALLS | chiamata diretta (anche getter/setter con logica e costruttori); `via = mediatr` da `Send`/`Publish` all'handler |
| DISPATCHES_TO | da metodo di interfaccia/virtuale all'implementazione: fa attraversare la DI |
| HANDLED_BY, EXPOSES | rotta -> action, metodo o pagina; controller, pagina, tipo o progetto -> rotta |
| RENDERS, REDIRECTS_TO | action -> vista; metodo o pagina -> rotta (RedirectToAction, Response.Redirect, Server.Transfer) |
| READS, WRITES, CALLS_PROC | metodo, pagina, procedura, funzione, trigger o vista -> tabella / procedura; `via` = `ef`, `schema`, `ef-tracking` (euristico) o `sql` (euristico) |
| REFERENCES, TRIGGERS | tabella -> tabella (chiave esterna); tabella -> trigger che scatta sulle sue scritture |
| SCHEDULES, SENDS_MESSAGE | metodo -> rotta del job o del timer che pianifica; metodo -> rotta dell'handler del messaggio che invia |
| USES_CONFIG | metodo -> chiave di configurazione, con la riga |
| HANDLES_EVENT | pagina -> gestore di evento nel code-behind (Page_Load, btnSave_Click, ...), con controllo ed evento |
| CODE_BEHIND, USES_CONTROL, USES_MASTER | pagina -> classe, controllo utente (.ascx), master page |
| USES_MODEL, LINKS_TO, POSTS_TO, RENDERS_PARTIAL, USES_LAYOUT, RENDERS_ACTION | dalle viste Razor e dalle pagine |
| INHERITS, IMPLEMENTS, CONTAINS, USES_TYPE, INSTANTIATES, MAPPED_TO, DECLARES_TABLE | struttura |

Le autorizzazioni di una rotta comprendono gli attributi ereditati da controller e metodi base
(`Authorize(Roles=Admin) [ereditato da BaseController]`), `[PrincipalPermission]` e le regole `<authorization>` dei
web.config per pagine, servizi `.svc` e handler HTTP. Le update di Entity Framework fatte per change tracking
(`entita.Campo = valore; SaveChanges()` oppure `Entry(x).State = Modified`) diventano archi WRITES con `via = ef-tracking`.

### Web Forms

Il markup di `.aspx`, `.ascx`, `.master`, `.asmx`, `.ashx`, `.svc` e `Global.asax` si legge con espressioni regolari,
come le viste Razor; il code-behind e' gia' nel grafo e viene collegato alla pagina tramite `Inherits`/`Class`/`Service`.

- Entry point: una rotta per ogni pagina `.aspx` (GET e POST), per ogni `[WebMethod]` di un `.asmx`, per ogni
  operazione di un `.svc`, per il `ProcessRequest` di un `.ashx` e per gli eventi `Application_*` e `Session_*` di
  `Global.asax`. Gli URL di `MapPageRoute` diventano `friendly_urls` della rotta della pagina.
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
INDEX.md                      punto di partenza: numeri, progetti, entry point, dati, report, qualita' dell'estrazione
routes/INDEX.md               gruppi di entry point (controller, cartelle Web Forms, servizi, batch, job, messaggi, ...)
routes/<gruppo>.md            rotte del gruppo, con autorizzazioni e tabelle scritte
routes/<gruppo>/<rotta>.md    dossier: metodi raggiunti, dati, logica nel database, vincoli delle tabelle scritte, viste e
                              pagine, redirect, integrazioni, stato implicito, configurazione con i valori, validazioni
                              (anche dei DTO), eccezioni (anche RAISERROR), messaggi e job, aggancio per i test
types/INDEX.md                progetti
types/<progetto>/INDEX.md     namespace e tipi
types/<progetto>/<tipo>.md    metodi e proprieta': firma, posizione, attributi, fatti estratti, chiamati, chiamanti, rotte
tables/INDEX.md               tabelle, viste, stored procedure, funzioni e trigger
tables/<tabella>.md           colonne con il dizionario dati, vincoli, chiavi esterne, trigger, chi legge e chi scrive
tables/sp-, fn-, tr-<nome>.md procedure, funzioni, trigger: parametri, cosa leggono e scrivono, eccezioni, chi li usa
views/INDEX.md                viste Razor e file Web Forms per progetto
views/<progetto>.md           modello o code-behind, controlli, campi, validatori, gestori di evento, form e link
reports/authorization.md      autorizzazioni per gruppo e rotte raggiungibili dalla rete senza autorizzazione dichiarata
reports/data-ownership.md     tabelle scritte da piu' gruppi di entry point: i confini da decidere per i microservizi
reports/domains.md            domini candidati: gruppi di entry point raggruppati per dati usati (base per la capability map)
reports/database.md           contenuto dello schema, logica nel database, SQL dinamico, oggetti non raggiunti
reports/configuration.md      chiavi di configurazione con i valori per file e ambiente, chiavi senza valore
reports/implicit-state.md     Session, ViewState, TempData, ViewBag, Cache
reports/integrations.md       email, http, wcf, code, file system, sql
reports/hotspots.md           metodi piu' complessi e lunghi, logica nel markup
reports/gaps.md               non risolti, avvisi di caricamento, schema SQL mancante o con errori, oggetti assenti dallo schema
```

Il dizionario dati di una tabella collega ogni colonna alla proprieta' dell'entita' mappata (per nome o `[Column]`) e ai
campi in ingresso con lo stesso nome (campi delle viste e delle pagine, parametri della richiesta, proprieta' dei DTO
delle action e delle API). La corrispondenza per nome e' un'euristica, dichiarata come tale: il percorso reale del valore
(assegnazioni e mapping) va verificato nel codice.

L'aggancio per i test dice se l'handler di una rotta si puo' testare in processo o se servono HTTP o la UI (stato
implicito, pagine Web Forms) e, in quel caso, quali metodi raggiunti sono testabili in processo, con cio' che serve
(database, chiavi di configurazione, integrazioni da sostituire).

Pensata per progetti grandi:

- un file per rotta e un indice per ogni livello;
- le classi con piu' di 60 metodi divise in file da 40 (`<tipo>.2.md`, ...);
- elenchi lunghi troncati con "... e altri N";
- il codice non viene copiato, solo file e riga.

Su un grafo sintetico da 26.000 nodi e 106.000 archi produce circa 6.000 file in una ventina di secondi, con 320 MB di memoria.

La cartella viene rigenerata a ogni esecuzione: si cancellano solo `INDEX.md` e le sottocartelle elencate sopra, e solo
se la cartella e' vuota o contiene il file `.graph-docs` lasciato da un'esecuzione precedente (altrimenti serve `--force`).

## 3. Collegare l'agente

Il kit ha gli stessi pezzi per ogni agente:

- `agent/AGENTS.md`: istruzioni sempre attive (fonti, regole di lavoro, catalogo delle regole, capability map e
  glossario, formato dei deliverable, verifiche);
- `agent/skills/`: le skill, nel formato aperto [Agent Skills](https://agentskills.io):
  - `as-is`: ricostruisce un caso d'uso e le sue regole;
  - `as-is-tests`: scrive ed esegue i test di caratterizzazione del legacy partendo dalle regole;
  - `capability-map`: costruisce la capability map dai domini candidati e la confronta con gli esperti, se ci sono.

Copiali nel repository legacy dove li cerca il tuo agente. Se il file di istruzioni esiste gia', aggiungi il contenuto
in fondo invece di sostituirlo.

| Agente | Istruzioni | Skill |
| --- | --- | --- |
| Claude Code | `CLAUDE.md` | `.claude/skills/<skill>/` |
| GitHub Copilot | `.github/copilot-instructions.md` | `.github/skills/<skill>/` |
| Entrambi | `.github/copilot-instructions.md`, piu' un `CLAUDE.md` con la sola riga `@.github/copilot-instructions.md` | `.claude/skills/<skill>/`, che legge anche Copilot |

Per Copilot le istruzioni vanno in `.github/copilot-instructions.md` e non in `AGENTS.md`: e' l'unico file letto da
tutti i client (VS Code, Visual Studio, JetBrains, Copilot CLI, agente cloud).

Uso: l'agente scrive in `docs/as-is/` (casi d'uso, catalogo delle regole, capability map, glossario) e in `tests/as-is/`
(scenari e binding). In Copilot Chat serve la modalita' agente, perche' deve scrivere file.

- Claude Code, Copilot in VS Code e Copilot CLI: `/as-is Orders`, `/as-is-tests Gestione ordini`, `/capability-map`.
- Visual Studio 2026 (18.5 o successivo) e JetBrains: chiedi in chat, es. "ricostruisci la specifica as-is di Orders";
  l'agente attiva la skill dalla sua descrizione.
- Visual Studio 2022, che non legge le skill: copia anche `agent/prompts/*.prompt.md` in `.github/prompts/`, poi in
  chat scrivi `#prompt:`, scegli il prompt e aggiungi la richiesta. I prompt rimandano alle skill, che vanno copiate lo stesso.
- Agente cloud di Copilot (repository su GitHub, con `docs/graph` committata): assegnagli una issue con la richiesta;
  apre una pull request. Non puo' fare domande durante il lavoro: le skill gli dicono di proseguire e di riportare
  elenchi e domande nel resoconto.

Per i controlli, indica all'agente (nel file di istruzioni o nel README del repository legacy) dove sono gli strumenti
del kit e `graph.json`, per esempio `python tools/code-graph/check_asis.py --graph docs/graph/graph.json --root .`.

**Altri agenti:** `AGENTS.md` nella radice del repository (lo leggono Codex, Cursor e molti altri) e le skill nella
cartella delle skill dell'agente; se l'agente non supporta le skill, usa i `SKILL.md` come prompt.

Input che conviene dare all'agente oltre al codice e allo schema:

- i valori di configurazione di produzione (spesso non sono nel repository): l'agente li chiede; se mancano, le regole
  dichiarano il valore ipotizzato;
- i dati di utilizzo (log IIS, Application Insights): dicono quali funzioni sono usate e quali documentare prima; se
  non ci sono, nessuna funzione si puo' dichiarare inutilizzata;
- un estratto anonimizzato del database per i test; se non c'e', la skill `as-is-tests` genera dati coerenti con lo
  schema e con le regole.

## 4. Verificare i deliverable

La documentazione as-is vale quanto le sue evidenze. Quattro livelli, dal piu' economico:

1. **Controlli deterministici** (`check_asis.py`, anche in CI): formato del catalogo, id e file:riga che esistono e
   cadono nei nodi citati, CONFIRMED con righe di sorgente, GAP con la domanda, archi euristici confermati da una riga,
   valori citati nella regola (numeri, percentuali, testi) presenti nelle righe o nei nodi citati, versione del grafo.
   E il recall: per ogni caso d'uso, i fatti del dossier (validazioni, eccezioni, vincoli CHECK, scritture, procedure,
   configurazione) che nessuna regola spiega e che non sono esclusi con un motivo.

   ```bash
   python tools/check_asis.py --graph graph.json --docs docs/as-is --tests tests/as-is
   ```

   Scrive `docs/as-is/verifica/controlli.md`; esce con 1 se ci sono errori (`--strict`: anche con avvisi).

2. **Verifica indipendente con un LLM** (`verify_rules.py`, facoltativa): per ogni regola un modello vede solo il testo
   e le righe citate (piu' valori di configurazione e vincoli degli oggetti citati) e risponde supportata, parziale, non
   supportata o evidenza insufficiente. Usa un modello diverso da quello che ha scritto le regole, o almeno una sessione
   diversa. Non modifica il catalogo: le discordanze finiscono in `docs/as-is/verifica/llm.md` per una persona.

   ```bash
   python tools/verify_rules.py --graph graph.json --rules docs/as-is/rules.md --provider anthropic
   ```

3. **Test sul legacy** (skill `as-is-tests`): uno scenario Gherkin per regola, con gli esempi ai confini; binding per il
   legacy scelti dall'aggancio del dossier (in processo, HTTP, UI); database di test creato dallo schema SQL con dati
   forniti o generati. Un test che fallisce e' un risultato: la regola o la nostra comprensione sono sbagliate. Gli
   stessi `.feature` si rieseguono sul nuovo sistema con altri binding. Gli esiti vanno nella colonna Verifica.

4. **Revisione degli esperti di dominio**, se ci sono: tutte le regole GAP e INFERRED, quelle segnalate dai controlli e
   un campione delle CONFIRMED, con punteggi da 1 a 4 su veridicita', completezza, leggibilita' e utilita' nella colonna
   Revisione. Senza esperti, la verifica la fanno i test.

## 5. Provare con l'app d'esempio

```bash
python tools/graph_docs.py --graph sample/graph.sample.json --out sample/docs-graph
python tools/check_asis.py --graph sample/graph.sample.json --docs sample/docs/as-is --tests sample/tests/as-is
python tools/verify_rules.py --graph sample/graph.sample.json --rules sample/docs/as-is/rules.md --provider fake
```

`sample/docs/as-is` e `sample/tests/as-is` sono un esempio di deliverable: catalogo, caso d'uso, capability map,
glossario e scenari, nella lunghezza e nel formato attesi.

`sample/graph.sample.json` si rigenera con l'estrattore vero, anche senza Windows: `SampleGraph` carica i progetti
d'esempio in memoria e sostituisce `System.Web`, Entity Framework 6 e le librerie di integrazione con stub minimi
(`sample/SampleGraph/stubs`).

```bash
dotnet run --project sample/SampleGraph -c Release -- sample/app sample/graph.sample.json --sql sample/app/Database
```

## 6. Arricchimento con LLM (facoltativo)

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
- `--provider` e' obbligatorio: `anthropic`, `openai` e `copilot` inviano il sorgente al modello, `fake` non fa chiamate
  di rete. `--model` serve sempre con `openai`; con `copilot`, se manca, decide Copilot.
- `--scope` limita il lavoro ai metodi raggiungibili da rotte o controller che contengono il testo dato.
- L'esecuzione e' incrementale: un metodo si rielabora solo se cambia il suo sorgente o i riassunti dei chiamati,
  anche in presenza di ricorsione mutua; una rotta solo se cambia il suo dossier. Un riassunto `fake` non vale come
  cache per un provider reale.
- `--max-methods N` per prove e stime di costo (`0` non elabora nulla).
- Gli errori non recuperabili (credenziali, richiesta non valida, risposta troncata da `--max-tokens`) non vengono ritentati.
- `--provider openai --base-url ...` per endpoint compatibili OpenAI (Azure OpenAI, modelli on-premise).
  Per Bedrock o Vertex basta sostituire il client in `AnthropicLLM` con `AnthropicBedrock` / `AnthropicVertex`.
- `--provider copilot` usa la sottoscrizione GitHub Copilot tramite il Copilot SDK (`pip install github-copilot-sdk`,
  Python 3.11 o successivo), con l'utente autenticato in Copilot CLI oppure il token in `COPILOT_GITHUB_TOKEN`.
  `--model` prende gli id di Copilot (es. `claude-sonnet-4.5`, `gpt-5`): un id sconosciuto ferma lo script con l'elenco
  dei modelli disponibili. Ogni prompt e' una sessione senza strumenti, istruzioni del repository e memoria, e la
  telemetria GitHub del runtime e' disattivata. `--max-tokens` non si applica.
  Al primo uso l'SDK scarica il runtime di Copilot dalle release di GitHub: in azienda preparalo prima con
  `python -m copilot download-runtime` (o indica un runtime approvato con `COPILOT_CLI_PATH`) e imposta
  `COPILOT_SKIP_CLI_DOWNLOAD=1`.
- Metodi banali (accessor, deleghe di una riga) vengono riassunti senza LLM.

## 7. Neo4j (facoltativo)

Con `--neo4j` l'estrattore scrive `nodes.csv` ed `edges.csv`. Esempi di query dopo l'import:

```cypher
// tabelle scritte da piu' controller: candidate a conflitti di proprieta' tra servizi
MATCH (r:Route)-[:HANDLED_BY]->(:Method)-[:CALLS|DISPATCHES_TO*0..8]->(m:Method)-[:WRITES]->(t:Table)
WITH t, collect(DISTINCT split(r.id, '/')[1]) AS controllers
WHERE size(controllers) > 1 RETURN t.name, controllers ORDER BY size(controllers) DESC;

// pagine Web Forms che scrivono su una tabella, direttamente o dai gestori di evento
MATCH (p:Page)-[:HANDLES_EVENT|USES_CONTROL*0..2]->(x)-[:CALLS|DISPATCHES_TO*0..6]->()-[:WRITES]->(t:Table)
RETURN p.id, collect(DISTINCT t.name);

// trigger che scattano quando un entry point scrive una tabella
MATCH (r:Route)-[:HANDLED_BY]->()-[:CALLS|DISPATCHES_TO|CALLS_PROC*0..8]->()-[:WRITES]->(:Table)-[:TRIGGERS]->(tr:Trigger)
RETURN r.id, collect(DISTINCT tr.name);
```

## Limiti noti

- Reflection, `dynamic`, risoluzione DI per nome o factory: le chiamate non risolte finiscono in `unresolved_calls`.
- Rotte convenzionali personalizzate in `RouteConfig` non vengono lette: si assume `{controller}/{action}`.
- Autorizzazione: non vengono letti i filtri globali (`FilterConfig`, `GlobalFilters`) e i controlli nel codice
  (`User.IsInRole`, verifiche manuali), anche quando stanno in un modulo HTTP. Le action pubbliche ereditate da un
  controller base non diventano rotte del controller derivato.
- Entity Framework: le update per change tracking sono dedotte (`ef-tracking`) solo su entita' mappate in un DbContext,
  senza verificare che poi venga chiamato `SaveChanges`; `Set<T>()` con `T` generico (repository generici) non dice
  la tabella e finisce in `unresolved_calls`.
- SQL nel codice costruito a pezzi: rilevamento parziale e marcato come euristico. Nello schema, l'SQL dinamico delle
  procedure (`sp_executesql`, `EXEC(@sql)`) e' segnalato ma non analizzato. Le funzioni chiamate dal codice C# non
  diventano archi.
- Il dizionario dati collega colonne, proprieta' e campi per nome: non segue il valore nelle assegnazioni e nei mapping.
- Job pianificati fuori dal codice (SQL Server Agent, Utilita' di pianificazione di Windows) e registrazioni Quartz
  fatte con la configurazione XML non vengono lette: gli `IJob` diventano comunque entry point, senza pianificazione.
- JavaScript nelle viste: solo gli URL ajax letterali vengono rilevati. `Web.sitemap` e i menu non vengono letti
  dall'estrattore (li legge l'agente per la capability map).
- Web Forms: i progetti "web site" (`CodeFile`, `App_Code`, senza csproj) non sono supportati; il codice scritto
  direttamente nei file `.ashx`/`.asmx` o in `<script runat="server">` non e' compilato e non entra nel grafo
  (viene solo contato); non si risolvono i controlli registrati in `web.config` (`<pages><controls>`) e gli URL che
  includono la virtual directory.
- Configurazione: si leggono solo `appSettings` di web.config, app.config e delle loro trasformazioni; file esterni
  (`configSource`, `file`) sono segnalati, variabili di ambiente e pipeline di rilascio no.
- I dati di utilizzo (log IIS, Application Insights) non vengono importati nel grafo.
- VB.NET non e' coperto.

## Sicurezza e riservatezza

- `enrich.py` e `verify_rules.py` inviano codice sorgente al modello, e lo stesso fa l'agente quando legge i file. Usa
  un endpoint approvato dalla tua azienda e verifica le policy prima di analizzare codice proprietario.
- Prima dell'invio maschera con `***` i valori di password, chiavi e token che trova nelle stringhe del codice
  (connection string, assegnazioni a variabili come `apiKey`). E' una rete di sicurezza, non una garanzia:
  `--no-redact` la disattiva.
- L'estrattore carica i progetti con MSBuild, che esegue i target dei csproj: lancialo solo su repository fidati,
  come faresti per compilarli. Gli script SQL vengono solo letti, mai eseguiti.
- Dei file di configurazione si leggono solo le `appSettings`; i valori di chiavi che sembrano segreti (password, token,
  chiavi) sono mascherati, delle connection string si tengono solo i nomi.
- Il contenuto del codice legacy (commenti, stringhe, script) arriva nel contesto dell'agente: `agent/AGENTS.md` gli
  chiede di trattarlo come dato e di non eseguire istruzioni trovate nei sorgenti.
- I test di caratterizzazione girano su un database di test creato dallo schema, mai su produzione; i dati reali vanno
  anonimizzati prima di usarli.
- `codegraph.py` e gli strumenti di verifica leggono solo file dentro la radice dei sorgenti, anche se `graph.json` o il
  catalogo indicano percorsi esterni.
- `docs/graph` descrive struttura del codice e del database, query SQL, valori di configurazione non segreti e nomi delle
  connection string (non i valori): trattala con la stessa riservatezza del sorgente.
