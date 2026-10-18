---
description: Ricostruisce la specifica as-is di un caso d'uso (rotta, controller o pagina) usando la documentazione del grafo
argument-hint: <rotta, controller o pagina, es. "POST /Orders/Create", "Orders" oppure "Customers/Edit.aspx">
---

Ricostruisci la specifica AS-IS per: $ARGUMENTS

Procedi cosi':

1. Trova le rotte coinvolte in `docs/graph/routes/INDEX.md` e negli indici dei gruppi, oppure cercando il testo in
   `docs/graph/routes/`. Mostrami l'elenco prima di continuare.
2. Per ogni rotta leggi il suo dossier (`docs/graph/routes/<gruppo>/<rotta>.md`). Individua i metodi con logica di
   dominio (calcoli, condizioni, validazioni, scritture su tabelle), ignorando infrastruttura e mapping banali.
3. Per quei metodi apri la scheda (link dal dossier) e poi il sorgente al file:riga indicato. Se un metodo e' troppo
   grande, procedi per blocchi. Per le pagine Web Forms leggi anche la scheda in `docs/graph/views/` (validatori,
   origini dati, gestori di evento) e il markup della pagina.
4. Registra ogni regola di business in `docs/as-is/rules.md` con evidenze (id dei nodi e file:riga) e stato
   CONFIRMED / INFERRED / GAP, capability = area funzionale.
5. Per ogni tabella scritta apri `docs/graph/tables/<tabella>.md`: chi altro la scrive? Annotalo, serve per i confini
   dei microservizi (vedi anche `docs/graph/reports/data-ownership.md`).
6. Scrivi `docs/as-is/<capability>/<caso-d-uso>.md` con le sezioni indicate in CLAUDE.md.
7. Chiudi con: numero di regole per stato, domande aperte per gli esperti di dominio, rischi per la migrazione
   (stato implicito, System.Web, stored procedure, integrazioni, logica nelle viste e nel markup, autorizzazioni da web.config).
