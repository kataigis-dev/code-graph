---
name: as-is
description: Ricostruisce la specifica as-is (caso d'uso e regole di business) di un entry point del monolite ASP.NET leggendo la documentazione del grafo in docs/graph, codice e schema SQL. Usala quando si chiede di documentare, analizzare o fare reverse engineering di un caso d'uso, una capability, un controller MVC/Web API, una pagina Web Forms, un servizio WCF, un batch, un job o un handler di messaggi. Argomento, es. "POST /Orders/Create", "Orders", "Customers/Edit.aspx" oppure "JOB fatturazione-notturna".
---

Ricostruisci la specifica AS-IS del caso d'uso indicato nella richiesta: una rotta (`POST /Orders/Create`), un
controller (`Orders`), una pagina (`Customers/Edit.aspx`) o un altro entry point (`JOB fatturazione-notturna`).
Se non e' indicato, chiedilo: non sceglierlo tu.

Segui le istruzioni del repository (regole di lavoro, catalogo delle regole, formato dei deliverable). Se nessuno
puo' risponderti durante il lavoro (agente cloud, esecuzione non interattiva), non fermarti alle conferme: scrivi
l'elenco delle rotte e le domande (anche sui valori di configurazione) all'inizio del resoconto e prosegui.

Procedi cosi':

1. Leggi `docs/as-is/rules.md`, `capabilities.md`, `glossary.md` e, se esiste, la cartella della capability in
   `docs/as-is/`: aggiorna cio' che c'e' invece di riscriverlo. Poi trova le rotte coinvolte in
   `docs/graph/routes/INDEX.md` e negli indici dei gruppi, oppure cercando il testo in `docs/graph/routes/`.
   Mostrami l'elenco prima di continuare: e' la lista di controllo che chiuderai alla fine.
2. Per ogni rotta leggi il suo dossier (`docs/graph/routes/<gruppo>/<rotta>.md`). Individua i metodi con logica di
   dominio (calcoli, condizioni, validazioni, scritture su tabelle), ignorando infrastruttura e mapping banali.
   La riga "Tipo" del dossier dice come leggere la rotta:
   - `mvc`, `webapi`, `aspnetcore`: action, modello del binding (le validazioni dei DTO sono nel dossier), filtri,
     vista restituita e redirect.
   - `convention`: riconosciuta solo dal nome; verifica in `RouteConfig` che sia raggiungibile prima di documentarla.
   - `webforms`: la scheda in `docs/graph/views/` (controlli, validatori, origini dati, gestori di evento), il markup
     della pagina, poi il code-behind nell'ordine del ciclo di vita: `Page_Init`/`OnInit`, `Page_Load` (separa il ramo
     `!IsPostBack`), gestori di evento dei controlli, `PreRender`. Includi master page e controlli utente.
   - `asmx`, `ashx`, `httphandler`, `wcf`, `signalr`: parametri del metodo o della richiesta; l'autorizzazione del
     dossier viene da web.config, `[PrincipalPermission]` o `[Authorize]`: cerca anche i controlli nel codice.
   - `batch`, `job`, `timer`, `service`, `message`: l'attore e' il sistema. Conta quando parte (riga "Dettagli": la
     pianificazione; chi lo pianifica o invia il messaggio e' nella sezione "Messaggi e job" del mittente), con quali
     valori di configurazione, cosa succede se fallisce o gira due volte (spesso e' una domanda aperta).
   - `global`, `httpmodule`, `owin`: valgono per tutte le richieste; se toccano la capability (autenticazione,
     sessione, errori, autorizzazioni nel codice) descrivili come comportamento trasversale, non come caso d'uso.
3. Per quei metodi apri la scheda (link dal dossier) e poi il sorgente al file:riga indicato. Se un metodo e' troppo
   grande, procedi per blocchi. Se il dossier dice "Dossier troncato" o un elenco finisce con "... e altri N",
   prosegui dalle schede dei metodi piu' profondi: il dossier da solo non e' completo.
4. Leggi anche la logica nel database: le sezioni "Logica nel database", "Vincoli del database sulle tabelle scritte" ed
   "Eccezioni" del dossier, poi le pagine di stored procedure, funzioni e trigger e il loro script SQL al file:riga.
   Un CHECK, un trigger che rifiuta l'operazione o un RAISERROR in una procedura sono regole del caso d'uso.
5. Configurazione: per ogni valore che decide una regola usa la sezione "configurazione" del dossier. Se manca il valore
   di produzione, chiedilo; se non e' disponibile, scrivi la regola con il valore del repository dichiarato come ipotesi.
6. Registra ogni regola in `docs/as-is/rules.md` con evidenze (id dei nodi e file:riga), stato CONFIRMED / INFERRED /
   GAP e capability (un livello 2 di `capabilities.md`; se manca, proponila li'). Aggiungi al glossario i termini nuovi.
7. Per ogni tabella scritta apri `docs/graph/tables/<tabella>.md`: chi altro la scrive, con quali vincoli e trigger?
   Annotalo, serve per i confini dei microservizi (vedi `reports/data-ownership.md` e `reports/domains.md`). Un elenco
   senza altri scrittori non basta: guarda in `reports/gaps.md` le chiamate non risolte e in `reports/database.md`
   l'SQL dinamico.
8. Scrivi `docs/as-is/<capability>/<caso-d-uso>.md` con le sezioni del formato dei deliverable. Nella copertura
   escludi, uno per riga con il motivo, i fatti del dossier che non sono regole (`- escluso: file:riga — motivo`).
9. Se puoi eseguire comandi, lancia `check_asis.py` (vedi le istruzioni del repository) e correggi errori e avvisi.
10. Chiudi con: stato di ogni rotta della lista (completa, parziale con cosa manca, non analizzata con il motivo),
    numero di regole per stato, domande aperte per gli esperti di dominio, rischi per la migrazione
    (stato implicito, System.Web, stored procedure e trigger, SQL dinamico, integrazioni, logica nelle viste e nel
    markup, autorizzazioni da web.config). Non dire che la capability e' documentata se restano rotte parziali o
    non analizzate. Proponi la skill `as-is-tests` per verificare le regole sul legacy.

Con molte rotte e sub-agenti disponibili, dai a ciascuno un gruppo di rotte indipendenti (passi 2-5) e chiedigli
regole candidate con evidenze e stato, senza id e senza scrivere file. Gli id BR e i file in `docs/as-is/` li scrivi
solo tu, cosi' la numerazione resta unica.
