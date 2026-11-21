# DiskVital

Salute, temperatura e storico dei dischi, nello stile di MultiTool ed Erodyne Suite. Richiede Windows 10/11 (.NET Framework 4.8, già incluso) e i permessi di amministratore (servono per leggere i dati S.M.A.R.T.).

## Cosa fa
- Legge lo stato S.M.A.R.T. di **HDD e SSD SATA**, **SSD NVMe** e dei dischi nei **box USB** che lo lasciano passare (SAT). Con i driver Intel RST usa anche `SMART_RCV_DRIVE_DATA`.
- Giudizio **Buono / Attenzione / Critico / Sconosciuto** con il motivo e cosa fare, in italiano.
- Temperatura, ore di accensione, accensioni, dati scritti (e media al giorno), vita residua degli SSD, settori difettosi, spazio occupato dei volumi.
- Tabella degli attributi con la **spiegazione** di ciascuno (clic su una riga).

## In più rispetto a CrystalDiskInfo
- **Storico**: ogni lettura viene salvata (`%APPDATA%\DiskVital\storico`), con grafico della temperatura ed elenco degli eventi (es. "Settori riallocati: da 0 a 8", con data e ora).
- **Monitoraggio**: interruttore nella barra laterale. Crea un'attività pianificata all'accesso (privilegi elevati, niente richiesta UAC, funziona anche a batteria), controlla i dischi ogni 10 minuti dall'area di notifica e avvisa se un disco peggiora o scalda troppo. Solo con DiskVital installato in Programmi.
- **Rapporto**: "Copia rapporto" (Ctrl+C) per un disco, "Esporta rapporto" in HTML o testo per tutti.
- Soglie di temperatura per tipo di disco; per gli NVMe quelle dichiarate dal produttore.

## Installazione
- Dalla **Erodyne Suite** (consigliato), oppure aprendo `DiskVital-Setup-<versione>.exe`: chiede se installare in Programmi (collegamenti nel menu Start e sul desktop, voce in App installate) o usarlo senza installare.
- `DiskVital-Setup-<versione>.exe /S`: installazione silenziosa (usata dalla suite). Aggiorna anche una versione già installata e riavvia il monitoraggio se attivo.
- Disinstallazione: da **App installate › DiskVital** (`--disinstalla`, `/S` silenzioso). Lo storico resta, a meno di sceglierne l'eliminazione.

## Limiti
- Alcuni box USB (soprattutto NVMe-USB con chip JMicron/Realtek) e alcuni controller RAID non lasciano passare i dati di salute: il disco compare come "Sconosciuto".
- Il "dato scritto" dei SATA SSD dipende dalle unità usate dal produttore: è una stima.

## Compilare
- Windows: `csc /target:winexe /win32icon:DiskVital.ico /win32manifest:app.manifest /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll /out:DiskVital-Setup-1.0.0.exe src\*.cs`
- GitHub: repo `Erodyne/DiskVital`, tag `v1.0.0`: `.github/workflows/release.yml` compila e pubblica la release.
- Poi firma per la suite: `Firma-Release.ps1 -File DiskVital-Setup-1.0.0.exe -Programma diskvital -Versione 1.0.0 -Chiave <erodyne-firma-PRIVATA.xml>` e carica il `.sig` nella release.
