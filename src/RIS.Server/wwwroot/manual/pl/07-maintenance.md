# Utrzymanie

## Gdzie są dane

| Lokalizacja | Zawartość |
|---|---|
| `%ProgramFiles%\RIS5` | Program, `cli\ris.exe` |
| `%ProgramData%\RIS5\appsettings.json` | Ustawienia maszyny (porty, HTTPS, role, retencja). Po zmianie uruchom usługę ponownie. |
| `%ProgramData%\RIS5\ris.db` | Konfiguracja: rejestracje aplikacji, instancje D365, serwery SFTP, zadania, zaszyfrowane sekrety, audyt |
| `%ProgramData%\RIS5\quartz.db` | Harmonogram (wyzwalacze) i historia uruchomień z logami |
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

## Strażnik bezczynnych zadań

Na stronie *Utrzymanie danych* (Konserwacja) można włączyć codzienne wstrzymywanie zadań, które przez ostatnie dni tylko kończą się błędem albo nic nie robią. **Domyślnie jest wyłączony** – przydaje się na serwerach testowych; na produkcji zadanie może celowo długo czekać na plik.

- **Dwa przełączniki**: *tylko błędy* (każde uruchomienie w okresie zakończyło się błędem) oraz *nic nie robi* (żadne uruchomienie nie wykonało pracy – tylko „Pominięte”, np. brak plików, i błędy). Na produkcji można włączyć sam pierwszy.
- **Okres** (domyślnie 7 dni) i **minimalna liczba planowanych uruchomień** (domyślnie 20). Sprawdzane są tylko zadania, które według harmonogramu uruchamiają się w okresie co najmniej tyle razy, czyli okres wielokrotnie przekracza ich rytm: co 5 minut = 2016 uruchomień w tydzień, co godzinę 8–16 pon–pt = 45, codziennie = 7 i raz w tygodniu = 1 (nigdy nie wstrzymywane).
- **Pomijane** są zadania wstrzymane, właśnie działające, utworzone, zmienione lub wznowione w okresie oraz oznaczone w edytorze (krok *Zaawansowane*) **Nie wstrzymuj automatycznie**.
- Na *tylko błędy* potrzeba też co najmniej max(3, planowane / 4) błędów z rzędu – to dowód, że zadanie naprawdę działało przez okres. *Nic nie robi* jest liczone od instalacji wersji, która zapisuje ostatnią pracę zadań (`ris.db`, tabela `JobActivity`), więc zaczyna działać tydzień po aktualizacji.
- **Pokaż, co zostałoby wstrzymane** – próba na sucho, nic nie wstrzymuje. **Sprawdź i wstrzymaj teraz** – od razu, według zapisanych ustawień. Codzienne sprawdzanie o ustawionej godzinie (domyślnie 06:00).
- Wstrzymanie trafia do audytu (kto: `RIS`), na pulpit („Wymaga uwagi”, z powodem) i do dziennika zdarzeń (**2100**). Zadanie wznawia się zwykłym *Wznów*; potem ma znowu cały okres, zanim strażnik je ruszy.
- Z linii poleceń: `ris idle-guard status`, `ris idle-guard on [--only failing|idle] [--days 7] [--min-runs 20] [--at 06:00]`, `ris idle-guard off`.

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
| 2100 | Zadanie wstrzymane przez strażnika bezczynnych zadań |
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
- **Eksport i ponowne ustawienie sekretów**: `ris db export -o jobs.json` na starym serwerze, `ris db import jobs.json` i ponowne wpisanie sekretów (w *Rejestracjach aplikacji* albo `ris db set-secret`) na nowym. Historia uruchomień zostaje na starym serwerze.

## Prace serwisowe

- **Wstrzymanie wielu zadań** na czas prac w D365: zaznacz na liście → *Wstrzymaj* z powodem (np. „Okno serwisowe D365 do 18:00”). Powód widzą wszyscy.
- **Start usługi bez uruchamiania zadań**: `Ris:SchedulerStartDelay` (np. `"01:00:00"`) – interfejs działa od razu, zadania ruszą po zadanym czasie.
- **Zatrzymanie usługi** przerywa trwające zadania w kilka sekund; Execution Id przerwanego eksportu jest w logu, a eksport w D365 może się dokończyć (nie jest powtarzany automatycznie).

## Linia poleceń (`ris.exe`)

| Polecenie | Do czego |
|---|---|
| `ris db export -o jobs.json` | Konfiguracja do pliku (sekrety jako `<SET-SECRET>`) |
| `ris db import jobs.json [--paused]` | Dodanie/aktualizacja rejestracji aplikacji, instancji D365, serwerów SFTP i zadań |
| `ris access list` / `add <rola> <konto>` / `remove <rola> <konto>` | Dostęp (role) – jak na stronie *Dostęp* |
| `ris idle-guard status` / `on` / `off` | Strażnik bezczynnych zadań – jak na stronie *Utrzymanie danych* |
| `ris db set-secret app-registration <nazwa>` | Ustawienie sekretu rejestracji aplikacji (wpisywany bez echa); `connection <nazwa>` ustawia sekret domyślnej rejestracji instancji |
| `ris convert-schedule Schedule.xml -o jobs.json` | Konwersja konfiguracji RIS 4.x |
| `ris validate jobs.json` | Sprawdzenie pliku konfiguracji i folderów |
| `ris run jobs.json --job <Grupa.Zadanie>` | Jednorazowe uruchomienie zadania z konsoli (testy) |
