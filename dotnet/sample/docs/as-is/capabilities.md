# Capability map

Costruita dal codice (domini candidati di `docs/graph/reports/domains.md`, nomi degli entry point, tabelle scritte).
Per l'esempio non ci sono esperti di dominio: tutte le capability sono "proposta (senza esperti)".

## Vendite

### Gestione ordini
Creazione, consultazione e approvazione degli ordini dei clienti.
- Entry point: `R:GET /Orders/Create`, `R:POST /Orders/Create`, `R:ANY /Orders/Details`, `R:HUB OrdersHub/GetOrder`, `R:GET /export/orders.csv`, `R:JOB ReminderJob.SendReminders`, `R:TIMER ReminderService.OnTick`, `R:MESSAGE PaymentReminder`
- Tabelle: Orders (condivisa con Fatturazione ordini e Anagrafica clienti), OrderLines
- Stato: proposta (senza esperti)

### Prezzi e sconti
Calcolo del totale di un ordine: sconto sopra soglia e IVA.
- Entry point: nessuno proprio, usata da Gestione ordini
- Tabelle: nessuna
- Stato: proposta (senza esperti)

### Fido cliente
Fido dei clienti e controllo del fido sugli ordini.
- Entry point: `R:POST /Services/Pricing.asmx/CreditLimit`, `R:WCF /Services/OrderService.svc/GetCreditLimit`
- Tabelle: nessuna (legge Customers)
- Stato: proposta (senza esperti)

## Anagrafiche

### Anagrafica clienti
Creazione, modifica e disattivazione dei clienti.
- Entry point: `R:GET|POST /Customers/Edit.aspx`, `R:POST /api/customers`, `R:GET /api/customers/{id}`
- Tabelle: Customers
- Stato: proposta (senza esperti)

## Fatturazione

### Fatturazione ordini
Fatturazione notturna degli ordini confermati e annullamento degli ordini non fatturati.
- Entry point: `R:JOB fatturazione-notturna`, `R:JOB InvoiceJob`, `R:WCF JobControlService/RunInvoicing`, `R:WCF /Services/OrderService.svc/CancelOrder`, `R:MAIN Shop.Batch`
- Tabelle: Invoices, Orders (condivisa con Gestione ordini)
- Stato: proposta (senza esperti)

## Piattaforma

### Accesso e sicurezza
Autenticazione, sessione e controlli trasversali sulle richieste.
- Entry point: `R:APP /Global.asax`, `R:MODULE AuditModule`, `R:OWIN Startup`, `R:SERVICE ReminderService`, `R:GET|POST /Admin/Users.aspx`
- Tabelle: nessuna
- Stato: proposta (senza esperti)

## Domande

- Chi approva gli ordini in attesa (BR-0008)? Se e' un altro sistema, Gestione ordini e' divisa tra due applicazioni.
- `vw_CustomerOrders` e `fn_CustomerExposure` non sono raggiunte da nessun entry point: le usa un report o un altro sistema?
