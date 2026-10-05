# windhawk-share

Strumento per condividere i propri setup di Windhawk: scegli quali mod e quali impostazioni
condividere, e ottieni un file JSON da pubblicare o passare ad altri.

## Regola fondamentale

Si condividono **solo mod del repository ufficiale** (`ramensoftware/windhawk-mods`), **non modificate**.

- Il formato del pacchetto non ha campi per codice, sorgenti o URL: contiene solo ID, versioni e valori.
- Le mod locali (`local@...`) e quelle non presenti nel repository vengono escluse.
- Se la versione installata è l'ultima, il sorgente installato viene confrontato con quello ufficiale
  (SHA-256): se è diverso, la mod viene esclusa.
- L'indirizzo del repository è fisso nel codice, mai letto da file esterni.
- Se il repository non è raggiungibile, l'esportazione si interrompe: senza verifica non si esporta.

## Requisiti

- **Windhawk 2.0 o successivo**: lo strumento usa `windhawk-cli.exe`, la riga di comando ufficiale
  introdotta con la 2.0 (al momento in alpha). Con la 1.7.x non funziona.
- Per compilare: .NET 10 SDK. Per usare l'eseguibile pubblicato non serve nulla.

## Compilazione

```
dotnet publish -c Release -r win-x64 -o publish
```

Produce `publish/windhawk-share.exe`, un unico file autonomo. Per PC ARM usa `-r win-arm64`.

## Uso

```
windhawk-share list
windhawk-share settings explorer-style
windhawk-share export -o mio-setup.json
windhawk-share export -o explorer.json --mod explorer-style --name "Explorer scuro" --author io
windhawk-share export -o explorer.json --mod explorer-style:Theme,controlStyles
```

Senza `--mod`, `export` avvia la scelta guidata: numeri delle mod, poi per ciascuna le impostazioni
(Invio = tutte).

Le impostazioni si scelgono per nome di primo livello: `TimeStyle` include `TimeStyle.FontSize`,
`TimeStyle.TextColor`, ecc. Le liste (`controlStyles[0]...`, `controlStyles[1]...`) si condividono
sempre intere, per evitare liste incoerenti.

## Importazione

```
windhawk-share import pacchetto.json
windhawk-share import pacchetto.json --only windows-11-file-explorer-styler
windhawk-share import pacchetto.json --exact-version
```

Prima di toccare qualsiasi cosa mostra il piano: quali mod verranno installate, quali solo aggiornate,
cosa verrà ignorato e perché, e le impostazioni che contengono percorsi, comandi o indirizzi web.
Si procede solo dopo conferma (oppure con `--yes`).

Regole dell'importatore:

- Le mod si installano **solo per ID dal repository ufficiale** con `windhawk-cli mod install <id>`.
  L'opzione `--file` della CLI non viene mai usata.
- ID non validi, mod `local@` e mod non presenti nel repository vengono saltati.
- Chiavi e valori vengono controllati prima di passarli alla CLI (niente `=` nelle chiavi, niente
  argomenti che iniziano con `-`, limiti di lunghezza); poi Windhawk li valida contro lo schema della mod.
- Se Windhawk rifiuta qualche impostazione (es. non esiste più nella versione installata), le altre
  vengono applicate lo stesso, gruppo per gruppo, e quelle rifiutate vengono elencate.
- Per le mod già installate cambiano solo le impostazioni presenti nel pacchetto; versione e stato
  attivo/disattivo restano come sono. Le mod nuove vengono installate attive o disattive come nel pacchetto.
- Di default si installa l'ultima versione della mod; con `--exact-version` quella del pacchetto.

## Da verificare su un vero Windhawk 2.0

Il codice si basa sulla documentazione della CLI, ma non è stato provato su Windhawk reale.
Controlla questi punti (sono tutti isolati in un solo posto nel codice):

1. ~~Posizione di `--json`~~ (verificato). **Posizione di `--json`** (`WindhawkCli.RunAsync`): si assume che vada prima del comando,
   es. `windhawk-cli --json mod list`. Se la CLI lo vuole alla fine, va spostato lì.
2. **Forma del JSON** di `mod list`, `mod settings get`, `repo show` (`WindhawkCli`):
   il parsing accetta sia un array diretto sia un oggetto con l'elenco dentro, e sia impostazioni
   piatte sia annidate, ma conviene confrontarlo con l'output reale.
3. ~~Prefisso delle mod locali~~: verificato, è `local@`.
4. **Cartella dei sorgenti** (`OfficialCheck.DefaultModsSourceDir`): si assume
   `%ProgramData%\Windhawk\ModsSource`. Per la versione portable usa `--mods-source`.
5. **Booleani**: si esportano come li restituisce la CLI e in importazione si passano come
   `true`/`false` (`ImportValidation.ToCliValue`).
6. **Installazione** (`WindhawkCli.InstallFromRepoAsync`): si assume che `mod install <id>` installi
   dal repository, con `--version` e `--disabled` facoltativi.

Comandi utili per controllare:

```
windhawk-cli --help
windhawk-cli --json mod list
windhawk-cli --json mod settings get <id-mod>
windhawk-cli --json repo show <id-mod>
```

## Perché non usare direttamente `windhawk-cli data export`

La funzione ufficiale di backup è pensata per trasferire i propri dati sul proprio PC, e l'archivio
può includere anche mod locali. Per pubblicare setup che chiunque può scaricare serve un formato che
non trasporti codice: è il motivo di questo progetto.

## Struttura

- `src/Models.cs`: formato del pacchetto
- `src/WindhawkCli.cs`: unico punto di contatto con Windhawk, tramite la CLI ufficiale
- `src/OfficialCheck.cs`: verifica con il repository ufficiale
- `src/SettingsTools.cs`: impostazioni piatte e selezione per nome
- `src/Exporter.cs`: costruzione del pacchetto
- `src/Importer.cs`: controlli sul pacchetto ricevuto, piano e applicazione
- `src/Program.cs`: comandi da riga di comando
