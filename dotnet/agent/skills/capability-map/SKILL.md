---
name: capability-map
description: Costruisce o aggiorna la capability map del monolite in docs/as-is/capabilities.md, cioe' le capacita' di business su due livelli con gli entry point e le tabelle di ciascuna. Parte dal grafo del codice (domini candidati, proprieta' dei dati, nomi degli entry point, menu dell'interfaccia) e la confronta con gli esperti di dominio, se ci sono. Usala quando si chiede la capability map, la mappa delle funzionalita', i domini o i confini dei futuri microservizi.
---

Costruisci o aggiorna `docs/as-is/capabilities.md`. Una capability dice cosa fa il business (gestione ordini,
fatturazione), non come lo fa il codice (OrdersController) ne' chi lo fa (l'ufficio vendite).

1. Leggi la capability map esistente, il catalogo delle regole e il glossario: aggiorna, non riscrivere.

2. Dal codice, dal basso:
   - `docs/graph/reports/domains.md`: gruppi di entry point raggruppati per i dati che usano, con le tabelle condivise;
   - `docs/graph/reports/data-ownership.md`: chi scrive ogni tabella;
   - `docs/graph/routes/INDEX.md`: nomi di controller, cartelle, servizi, job;
   - l'interfaccia: menu e link di master page e layout, `Web.sitemap`, titoli delle pagine, etichette;
   - lo schema SQL: nomi di schemi e tabelle, che spesso seguono le aree di business.
   I domini candidati sono un punto di partenza: unisci, dividi e rinomina in linguaggio di dominio.

3. Formato: capability di livello 1 come `## Nome`, di livello 2 come `### Nome`, ognuna con:
   - una riga di descrizione;
   - `Entry point:` gli id delle rotte (tutte le rotte vanno in una sola capability di livello 2);
   - `Tabelle:` quelle che scrive (se una tabella e' scritta da piu' capability, scrivi `condivisa con ...`);
   - `Stato:` proposta, oppure validata da chi e quando.
   Gli entry point trasversali (Global.asax, moduli HTTP, OWIN, autenticazione) vanno in una capability di livello 1
   "Piattaforma". Batch, job e handler di messaggi vanno nella capability di business che servono.

4. Con esperti di dominio: proponi la mappa e raccogli la loro (workshop, Event Storming), poi confronta.
   - Capability nel codice che nessuno nomina: funzione nascosta o inutilizzata (controlla i dati di utilizzo, se ci sono).
   - Capability nominate ma assenti dal codice: altro sistema, processo manuale o codice non analizzato.
   Ogni differenza diventa una domanda in fondo al file.

5. Senza esperti di dominio: confronta il risultato con fonti indipendenti dal codice (menu e testi dell'interfaccia,
   manuali e documentazione esistente, report, nomi nel database). Lascia lo stato "proposta (senza esperti)" e metti
   in fondo al file i dubbi da chiarire quando qualcuno sara' disponibile.

6. Coerenza: la colonna Capability di `rules.md` usa i nomi di livello 2. Se rinomini o dividi una capability,
   aggiorna quella colonna (solo quella) e i percorsi `docs/as-is/<capability>/`. Se puoi eseguire comandi, lancia
   `check_asis.py`.

7. Chiudi con: numero di capability per livello, entry point non assegnati, tabelle condivise (i confini da decidere
   per i microservizi), domande aperte.
