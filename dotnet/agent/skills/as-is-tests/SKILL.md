---
name: as-is-tests
description: Scrive ed esegue i test di caratterizzazione del sistema legacy a partire dal catalogo delle regole as-is. Gli scenari Gherkin sono una specifica condivisa con il nuovo sistema; i binding per il legacy usano il punto di aggancio indicato dal grafo (in processo, HTTP o UI) e dati di test coerenti con lo schema SQL. Usala quando si chiede di scrivere o eseguire test sul legacy per una capability o per delle regole, di verificare il catalogo o di preparare i test da rieseguire sul nuovo sistema. Argomento, es. "Gestione ordini" oppure "BR-0003, BR-0004".
---

Scrivi i test sul legacy per le regole indicate nella richiesta: una capability di `docs/as-is/capabilities.md` o degli
id del catalogo. Se non e' indicato, chiedilo. Segui le istruzioni del repository; scrivi solo in `tests/as-is/` e,
per gli esiti, nella colonna Verifica di `docs/as-is/rules.md`.

I test descrivono cio' che il legacy fa oggi, non cio' che dovrebbe fare. Ogni scenario parte da una regola del catalogo:
se passa, la regola e' osservata in esecuzione; se fallisce, la regola o la nostra comprensione del codice sono sbagliate.

1. Regole. Leggi le regole della capability: CONFIRMED e INFERRED si testano (il test decide), le GAP no (elencale nel
   resoconto). Per ogni regola leggi il caso d'uso che la cita e le sue evidenze.

2. Specifica condivisa: `tests/as-is/features/<capability>/<caso-d-uso>.feature`, in Gherkin italiano (`# language: it`).
   - Una Funzionalita' per caso d'uso; ogni scenario ha il tag della sua regola (`@BR-0003`) e del punto di aggancio
     (`@aggancio:servizio`, `@aggancio:http`, `@aggancio:ui`).
   - Solo linguaggio di dominio: niente nomi di classi, tabelle, URL, controlli o selettori. Lo stesso file deve poter
     girare sul nuovo sistema (microservizi e Angular).
   - Esempi ai confini scritti nella regola: il valore esatto della soglia, appena sotto e appena sopra; vuoto; lunghezza
     massima. Se la regola non dice il confine, e' ambigua: correggila prima di scrivere il test.
   - Pochi scenari, brevi: uno per comportamento, con gli esempi in tabella.

3. Punto di aggancio, dal grafo. Nel dossier della rotta la sezione "Aggancio per i test" dice se l'handler e' testabile
   in processo e, se no, quali metodi lo sono e che cosa serve (database, configurazione, integrazioni da sostituire).
   - `servizio`: la regola sta in un metodo testabile in processo (o in un oggetto del database raggiunto da li').
   - `http`: l'handler usa stato implicito, o la regola sta nel model binding (validazioni dei DTO e ModelState non
     girano se chiami il metodo direttamente), o nell'autorizzazione, nei filtri, nei moduli HTTP.
   - `ui`: pagine Web Forms con ViewState, postback o validatori lato client; usala solo se l'HTTP non basta.

4. Dati di test. Il database di test si crea dallo schema SQL del repository (gli script passati all'estrattore con
   `--sql`) su SQL Server o LocalDB, mai su un database di produzione. Ogni scenario prepara i suoi dati e li ripulisce
   (transazione annullata o database ricreato).
   - Valori: quelli forniti dal team (estratto anonimizzato), altrimenti generati coerenti con lo schema (tipi,
     NOT NULL, chiavi esterne nell'ordine giusto, CHECK, UNIQUE, default) e con le regole (confini, stati ammessi).
     Scrivi i dati generati nello scenario o nel Contesto, non nascosti nei binding.
   - Configurazione: usa i valori del dossier; se manca quello di produzione, usa quello del repository e dichiaralo
     nel Contesto come ipotesi ("la soglia sconto vale 1000 (valore di web.config)").
   - Integrazioni: server SMTP di prova, stub HTTP, code locali. Nessuna email o chiamata vera verso l'esterno.

5. Binding per il legacy: progetto `tests/as-is/legacy/` con la versione di .NET Framework dei progetti della solution,
   Reqnroll e il framework di test gia' usato nella solution (altrimenti NUnit). I `.feature` si includono come file
   collegati, non si copiano. Uno step si scrive una volta e chiama il punto di aggancio scelto: classi del legacy in
   processo, `HttpClient` verso l'applicazione su IIS Express o su un ambiente di test (con login), oppure Playwright.

6. Esecuzione. Se l'ambiente c'e' (Windows, .NET Framework, SQL Server o LocalDB), esegui i test. In `rules.md`,
   colonna Verifica: `scenario «nome»: passa (AAAA-MM-GG)`, `fallisce (cosa succede)` o `non eseguito (motivo)`.
   Senza ambiente scrivi i test, segna `non eseguito` e scrivi cosa serve per eseguirli.

7. Un test che fallisce e' un risultato. Rileggi le righe di evidenza e il codice che le chiama:
   - la regola descrive male il codice: correggi il testo della regola (stesso id) e lo scenario;
   - il codice fa una cosa diversa da cio' che dicono esperti, commenti o documenti: la regola descrive il codice
     (as-is) e la differenza va nelle domande aperte;
   - l'ambiente o i dati sono sbagliati: correggili.
   Non cambiare un'asserzione per far passare il test senza capire perche' falliva.

8. Riuso sul nuovo sistema: i `.feature` restano gli stessi; il nuovo sistema avra' i suoi binding in
   `tests/as-is/new/`. Se una differenza di comportamento e' voluta, lo scenario prende il tag `@to-be:<decisione>` con
   il riferimento alla decisione; non si cancella.

9. Chiudi con: regole testate ed esiti, regole senza test e perche', ipotesi sui dati e sulla configurazione, cosa serve
   per eseguire i test non eseguiti. Se puoi eseguire comandi, lancia `check_asis.py` con `--tests tests/as-is`.
