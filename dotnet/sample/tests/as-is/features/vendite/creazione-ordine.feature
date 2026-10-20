# language: it
@capability:gestione-ordini
Funzionalità: Creazione di un ordine
  Specifica condivisa tra il sistema legacy e il nuovo: gli scenari descrivono il comportamento as-is.

  Contesto:
    Dato un cliente attivo "Rossi" con fido 20000
    E la soglia sconto vale 1000 (valore di web.config: quello di produzione non e' disponibile)

  @BR-0001 @aggancio:http
  Scenario: Ordine senza righe
    Quando un utente Sales crea un ordine per "Rossi" senza righe
    Allora l'ordine non viene salvato
    E compare il messaggio "Inserire almeno una riga d'ordine"

  @BR-0002 @aggancio:http
  Schema dello scenario: Quantita' fuori intervallo
    Quando un utente Sales crea un ordine per "Rossi" con una riga di quantita' <quantita>
    Allora l'ordine <esito>

    Esempi:
      | quantita | esito             |
      | 0        | non viene salvato |
      | 1        | viene salvato     |
      | 999      | viene salvato     |
      | 1000     | non viene salvato |

  @BR-0003 @aggancio:servizio
  Schema dello scenario: Sconto alla soglia
    Quando si calcola il totale di righe con lordo <lordo>
    Allora il totale e' <totale>

    Esempi:
      | lordo  | totale  |
      | 999.99 | 1219.99 |
      | 1000   | 1159.00 |

  @BR-0004 @aggancio:servizio
  Schema dello scenario: Ordine oltre il fido
    Dato un cliente attivo "Bianchi" con fido <fido>
    Quando si piazza un ordine per "Bianchi" con totale <totale>
    Allora l'ordine <esito>

    Esempi:
      | fido    | totale  | esito             |
      | 1159.00 | 1159.00 | viene salvato     |
      | 1158.99 | 1159.00 | non viene salvato |

  @BR-0005 @aggancio:servizio
  Schema dello scenario: Approvazione sopra soglia
    Quando si piazza un ordine per "Rossi" con totale <totale>
    Allora lo stato dell'ordine e' "<stato>"

    Esempi:
      | totale   | stato           |
      | 10000.00 | Confirmed       |
      | 10000.01 | PendingApproval |

  @BR-0006 @aggancio:servizio
  Scenario: Cliente non attivo
    Dato un cliente non attivo "Verdi" con fido 20000
    Quando si piazza un ordine per "Verdi" con totale 100
    Allora il database rifiuta l'ordine con il messaggio "Cliente non attivo: ordine rifiutato"
