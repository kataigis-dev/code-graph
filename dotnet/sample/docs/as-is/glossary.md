# Glossario

| Termine | Significato | Nel codice | Nel database | Stato |
| --- | --- | --- | --- | --- |
| Fido | Importo massimo che un cliente puo' avere in un ordine | `Customer.CreditLimit`, `GetCreditLimit` | `Customers.CreditLimit` (0..50.000) | dal codice |
| Ordine in attesa di approvazione | Ordine sopra 10.000, non ancora confermato | `"PendingApproval"` | `Orders.Status` | dal codice |
| Soglia sconto | Lordo minimo per lo sconto del 5% | `DiscountThreshold` (appSettings) | - | dal codice |
