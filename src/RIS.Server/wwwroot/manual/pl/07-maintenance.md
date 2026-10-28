# Utrzymanie

## Gdzie są dane

| Lokalizacja | Zawartość |
|---|---|
| `%ProgramFiles%\RIS5` | Program, `cli\ris.exe` |
| `%ProgramData%\RIS5\appsettings.json` | Ustawienia maszyny (porty, HTTPS, role, retencja). Po zmianie uruchom usługę ponownie. |
| `%ProgramData%\RIS5\ris.db` | Konfiguracja: instancje D365, serwery SFTP, zadania, zaszyfrowane sekrety, audyt |
| `%ProgramData%\RIS5\quartz.db` | Harmonogram (wyzwalacze, wstrzymania) i historia uruchomień z logami |
| `%ProgramData%\RIS5\keys\` | Klucze szyfrujące sekrety – **bez nich sekrety są nie do odczytania** |
| `%ProgramData%\RIS5\logs\` | `ris-RRRRMMDD.clef` (log usługi), `jobs\<Grupa.Zadanie>\` (log każdego zadania) |
| `%ProgramData%\RIS5\backup\` | Kopie baz przed zmianą schematu i przed aktualizacją |

## Retencja i czyszczenie danych

*Utrzymanie danych* (rola Admin) pokazuje, co jest przechowywane, ile, od kiedy i jak długo, a także ostatnie i następne czyszczenie. **Wyczyść teraz** uruchamia czyszczenie od razu.

![Utrzymanie danych](img/maintenance.png)

| Ustawienie (`Ris:Retention`) | Domyślnie | Znaczenie |
|---|---|---|
| `RunHistoryDays` / `FailedRunHistoryDays` | 30 / 90 | Historia uruchomień (udane / nieudane) |
| `MaxRunsPerJob` / `MaxRuns` | 1000 / 200000 | Najnowsze uruchomienia na zadanie (błędy się nie liczą) / łącznie |
| `RunLogKilobytes` | 64 | Log jednego uruchomienia (najstarsze wiersze odpadają) |
| `AuditDays` | 365 | Audyt zmian konfiguracji (najnowszy wpis zostaje zawsze) |
| `UpgradeBackupsToKeep` / `SettingsBackupsToKeep` | 5 / 5 | Kopie przed aktualizacją / kopie `appsettings.json` |
| `CleanupAt` | 03:30 | Codzienne czyszczenie (również 10 min po starcie usługi) |
| `Ris:LogRetentionDays` | 30 | Pliki logów; puste foldery logów usuniętych zadań są kasowane |

Przykład zmiany w `%ProgramData%\RIS5\appsettings.json`:

```json
{
  "Ris": {
    "Retention": { "RunHistoryDays": 60, "AuditDays": 730 },
    "LogRetentionDays": 60
  }
}
```

Historię przycina na bieżąco sam scheduler; resztę – codzienne czyszczenie. Pliki w folderach zadań (In, Processed…) **nie są** czyszczone przez RIS.

## Logi

- **Log usługi** – `logs\ris-RRRRMMDD.clef` (JSON w każdej linii, format CLEF; czytelny np. w Seq albo `Get-Content … | ConvertFrom-Json`).
- **Log zadania** – `logs\jobs\<Grupa.Zadanie>\job-RRRRMMDD.clef` – wszystko, co dotyczy jednego zadania.
- **Log uruchomienia** – w szczegółach uruchomienia w interfejsie (do 64 KB na uruchomienie).
- Każdy wpis ma `JobKey`, `RunId` i, gdzie dotyczy, `ExecutionId`, `MessageId`, `File` oraz kod błędu `RIS-…`.

## Dziennik zdarzeń Windows

Log **RIS5** (*Podgląd zdarzeń → Dzienniki aplikacji i usług → RIS5*, 20 MB, najstarsze wpisy nadpisywane). Zapisywane są ostrzeżenia i błędy (`Ris:EventLogMinimumLevel`) oraz start i zatrzymanie usługi. Stałe identyfikatory do monitoringu:

| Id | Znaczenie |
|---|---|
| 1000 / 1001 | Usługa uruchomiona (wersja, konto, adresy) / zatrzymywana |
| 2000 | Uruchomienie zadania zakończone błędem lub częściowo |
| 10000 + 100 × kategoria + numer | Błąd z kodem RIS: CFG=1, AUTH=2, HTTP=3, EXP=4, IMP=5, UPL=6, DWN=7, MON=8, FS=9, SFTP=10, GEN=11 – np. RIS-HTTP-001 = **10301**, RIS-IMP-004 = **10504** |
| 3000 / 4000 | Pozostałe ostrzeżenia / błędy |

> Po aktualizacji z wersji, która pisała do logu *Application*, Windows przenosi wpisy do RIS5 dopiero **po restarcie komputera**.

## Monitoring

- `GET /health` – `Healthy` / `Degraded` / `Unhealthy`: działa scheduler, bazy i katalog danych są dostępne, a zadania z `Ris:Health:RequiredJobs` odniosły sukces w zadanym czasie, np.:

  ```json
  { "Ris": { "Health": { "RequiredJobs": { "Export jobs.CustInvoiceJour": "1.02:00:00" } } } }
  ```

  (stan *Unhealthy*, gdy eksport faktur nie zakończył się sukcesem od 26 godzin). Domyślnie `/health` nie wymaga logowania.
- Dziennik zdarzeń RIS5 (identyfikatory powyżej).
- `GET /api/v1/jobs` – `lastResult` i `consecutiveFailures` każdego zadania.

## Kopia zapasowa i odtworzenie

**Kopia**: cały katalog `%ProgramData%\RIS5`, co najmniej `ris.db`, `quartz.db` i `keys\`. Spójna kopia przy działającej usłudze:

```powershell
sqlite3 "$env:ProgramData\RIS5\ris.db" ".backup 'D:\Backup\ris.db'"
```

albo zatrzymaj usługę i skopiuj pliki. Konfiguracja bez sekretów (np. do repozytorium): `ris db export -o jobs.json`.

**Odtworzenie**: zatrzymaj usługę → podmień `ris.db`, `quartz.db`, `keys\` → uruchom usługę. Terminy przegapione w czasie przerwy są obsługiwane zgodnie z ustawieniem *Przegapione terminy*.

## Przeniesienie na inny serwer

Klucze sekretów są domyślnie chronione przez DPAPI komputera i nie działają na innym serwerze. Do wyboru:

- **Certyfikat** (dla serwerów, które mogą być odtwarzane): ten sam certyfikat z kluczem prywatnym na obu serwerach, `Ris:Data:KeyCertificateThumbprint` ustawione *przed* zapisaniem sekretów, następnie kopia katalogu danych;
- **Eksport i ponowne ustawienie sekretów**: `ris db export -o jobs.json` na starym serwerze, `ris db import jobs.json` i ponowne wpisanie sekretów (w *Instancjach D365* albo `ris db set-secret`) na nowym. Historia uruchomień zostaje na starym serwerze.

## Prace serwisowe

- **Wstrzymanie wielu zadań** na czas prac w D365: zaznacz na liście → *Wstrzymaj* z powodem (np. „Okno serwisowe D365 do 18:00”). Powód widzą wszyscy.
- **Start usługi bez uruchamiania zadań**: `Ris:SchedulerStartDelay` (np. `"01:00:00"`) – interfejs działa od razu, zadania ruszą po zadanym czasie.
- **Zatrzymanie usługi** przerywa trwające zadania w kilka sekund; Execution Id przerwanego eksportu jest w logu, a eksport w D365 może się dokończyć (nie jest powtarzany automatycznie).

## Linia poleceń (`ris.exe`)

| Polecenie | Do czego |
|---|---|
| `ris db export -o jobs.json` | Konfiguracja do pliku (sekrety jako `<SET-SECRET>`) |
| `ris db import jobs.json [--paused]` | Dodanie/aktualizacja instancji D365, serwerów SFTP i zadań |
| `ris db set-secret connection <nazwa>` | Ustawienie sekretu instancji D365 (wpisywany bez echa) |
| `ris convert-schedule Schedule.xml -o jobs.json` | Konwersja konfiguracji RIS 4.x |
| `ris validate jobs.json` | Sprawdzenie pliku konfiguracji i folderów |
| `ris run jobs.json --job <Grupa.Zadanie>` | Jednorazowe uruchomienie zadania z konsoli (testy) |
