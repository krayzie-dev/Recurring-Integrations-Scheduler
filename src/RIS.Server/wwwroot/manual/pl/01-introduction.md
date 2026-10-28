# Wprowadzenie

**Recurring Integrations Scheduler 5 (RIS 5)** to usługa Windows, która cyklicznie przesyła dane między folderami (lokalnymi, sieciowymi, SFTP) a **Dynamics 365 Finance & Operations (D365)**. Zastępuje RIS 4.x: zachowuje wszystkie jego funkcje, a dodaje m.in. przeglądarkowy interfejs, trwałą historię uruchomień, logi z kontekstem, kody błędów i kreator zadań.

Ta instrukcja jest dostępna w aplikacji (**Pomoc** w menu lub ikona ❔ w górnym pasku) po polsku i angielsku – język zmienia się razem z językiem interfejsu. Przycisk **Drukuj / PDF** w spisie rozdziałów pozwala zapisać rozdział jako PDF.

![Pulpit RIS 5](img/dashboard.png)

## Jak to działa

```
 Foldery / SFTP  ──►  RIS 5 (usługa Windows)  ──►  D365 F&O
                       │  harmonogram (Quartz)
                       │  historia uruchomień, logi
                       └─ interfejs WWW + REST API
```

- RIS działa jako usługa **RIS5** na serwerze Windows. Interfejs otwierasz w przeglądarce, np. `http://localhost:5080` na serwerze lub `https://serwer:5443` z innego komputera (jeśli włączono HTTPS).
- Logowanie odbywa się kontem Windows (domenowym). Uprawnienia wynikają z ról przypisanych grupom lub użytkownikom.
- Konfiguracja (połączenia, zadania, sekrety) jest w bazie `ris.db`, harmonogram i historia uruchomień w `quartz.db` – obie w `%ProgramData%\RIS5`.

## Podstawowe pojęcia

| Pojęcie | Znaczenie |
|---|---|
| **Zadanie (job)** | Jedna integracja: co, skąd, dokąd i kiedy. Ma nazwę i grupę (klucz `Grupa.Nazwa`). |
| **Typ zadania** | Eksport, Import, Wysyłka (Upload), Pobieranie (Download), Monitor wykonania, Monitor przetwarzania – patrz [Typy zadań](04-job-types.md). |
| **Połączenie** | Środowisko D365 i aplikacja Entra ID, którą RIS się loguje (client secret albo certyfikat). Wiele zadań korzysta z jednego połączenia. |
| **Projekt danych** | Projekt w D365 *Zarządzanie danymi*, który określa encje i format plików. Eksport i import RIS uruchamiają projekty danych. |
| **Zadanie cykliczne (recurring data job)** | Integracja cykliczna w D365 identyfikowana przez *activity ID*. Używają jej typy Wysyłka i Pobieranie. |
| **Monitor** | Zadanie sprawdzające wynik importu lub wysyłki w D365 i przenoszące plik do folderu „przetworzone” albo „błędy”. Tworzy parę z zadaniem importu lub wysyłki. |
| **Uruchomienie (run)** | Jedno wykonanie zadania – z wynikiem, czasem trwania, podsumowaniem, metrykami i logiem. |
| **Execution Id** | Identyfikator wykonania w D365. Pokazywany przy uruchomieniu – po nim znajdziesz wykonanie w D365. |

## Role

| Rola | Może |
|---|---|
| **Viewer** | Oglądać pulpit, zadania, harmonogram, historię i szczegóły uruchomień, eksportować listy. |
| **Operator** | To co Viewer oraz uruchamiać, wstrzymywać i wznawiać zadania (także zbiorczo). |
| **Admin** | Wszystko: zadania, połączenia, serwery SFTP, edycja zbiorcza, audyt, utrzymanie danych. |

Role przypisuje się w ustawieniach (instalator pyta o administratorów i operatorów) – patrz [Instalacja](02-installation.md#roles).

## Co nowego względem RIS 4.x

- Interfejs w przeglądarce (bez klienta WinForms i bez .NET Remoting), jasny i ciemny motyw, polski i angielski.
- Konfiguracja zapisywana od razu w bazie z audytem zmian (kto, kiedy, co) – bez ręcznego zapisywania `Schedule.xml`.
- Historia uruchomień z logiem każdego przebiegu, Gantt planu i historii, pulpit z alertami.
- Kreator zadań z opisami pól, testem połączeń, konstruktorem harmonogramu (także „co 10 minut od 8 do 16 w dni robocze”).
- Koperta pakietu importu generowana automatycznie z projektu danych (bez szablonu), również dla pakietów z wieloma encjami.
- Czytelne kody błędów (`RIS-…`), dziennik zdarzeń Windows ze stałymi identyfikatorami, endpoint `/health` dla monitoringu.
- Rytm harmonogramu nie przesuwa się po restarcie; przegapione terminy obsługiwane zgodnie z ustawieniem.
- Instalator z wyborem konta usługi, HTTPS i importem zadań z 4.x.

Szczegółowe porównanie: plik `docs/ris5/parity-4x.md` w repozytorium.
