# Creazione di un ordine

Rotta: `R:POST /Orders/Create` · Capability: Gestione ordini

- Attore: utente con ruolo Sales.
- Obiettivo: registrare un ordine per un cliente.
- Precondizioni: utente autenticato con ruolo Sales; il cliente esiste ed e' attivo (BR-0006).

## Flusso principale

1. L'utente sceglie il cliente e inserisce le righe (vista `V:Web/Views/Orders/Create.cshtml`).
2. Il sistema valida le righe (BR-0001, BR-0002) e calcola il totale (BR-0003).
3. Il sistema verifica il fido (BR-0004), assegna lo stato (BR-0005) e salva l'ordine.
4. Il sistema invia l'email di conferma (BR-0007) e mostra il dettaglio dell'ordine.

## Flussi alternativi

- Righe mancanti o quantita' non valida: la pagina si ripresenta con il messaggio (BR-0001, BR-0002).
- Totale oltre il fido: eccezione non gestita, nessun ordine salvato (BR-0004).
- Cliente non attivo: il database rifiuta l'inserimento (BR-0006).

## Dati, integrazioni, stato implicito

- Legge Customers (fido), scrive Orders; il trigger scrive AuditLog.
- Email via SMTP (`smtp.local`).
- Legge `Session["Cart"]` senza usarlo; scrive `TempData["Message"]`.

## Domande aperte

- Chi approva gli ordini in attesa (BR-0008)?
- Valore di produzione della soglia sconto (BR-0003).

## Copertura

- Versione del grafo: Generato da `tools/graph_docs.py` dal grafo `graph.sample.json` (roslyn-graph 0.3 (sample), data non indicata).
- Letti: dossier della rotta, OrdersController.Create, OrderService.PlaceOrder, PricingService.CalculateTotal, OrderRepository, trigger trg_Orders_Audit.
- escluso: Web/Controllers/OrdersController.cs:39 — `Session["Cart"]` letto e mai usato: nessun effetto sul caso d'uso.
- escluso: Database/Shop.sql:25 — CHECK sul totale non negativo: sempre vero, il totale nasce da quantita' e prezzi positivi.
- escluso: Database/Shop.sql:26 — CHECK sui valori dello stato: i valori assegnati sono quelli di BR-0005.
