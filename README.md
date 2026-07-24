# MultiInstaller personale

Applicazione Windows che consente di:

1. trascinare o scegliere più installer `.exe`, `.msi`, `.msix`, `.appx`;
2. conservarli automaticamente nella propria libreria locale;
3. selezionare i programmi tramite spunta;
4. premere **AVVIA INSTALLAZIONI** per eseguirli uno dopo l'altro.

Non è necessario modificare file JSON o configurare manualmente il catalogo.

## Compilazione online con GitHub

1. Carica tutti i file del progetto in un repository GitHub.
2. Apri **Actions**.
3. Seleziona **Compila MultiInstaller per Windows**.
4. Premi **Run workflow**.
5. Scarica l'artifact `MultiInstaller-Windows-x64`.

## Dati locali

Gli installer aggiunti vengono copiati in:

`%LOCALAPPDATA%\MultiInstaller\Installers`

Il catalogo viene salvato in:

`%LOCALAPPDATA%\MultiInstaller\catalog.json`

## Nota sugli installer silenziosi

I file MSI sono installati automaticamente in modalità silenziosa. Per diversi programmi comuni il software riconosce automaticamente i parametri corretti. Non esiste però un unico parametro silenzioso valido per ogni file EXE: quando un installer sconosciuto non supporta i parametri rilevati, potrebbe aprire la propria finestra guidata. L'installazione viene comunque avviata dal programma.
