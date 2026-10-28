# Instalacja

## Wymagania

- Windows Server 2016 lub nowszy (lub Windows 10/11 do testów), 64-bit. **.NET nie trzeba instalować** – program zawiera własne środowisko.
- Uprawnienia administratora lokalnego do instalacji.
- Dostęp sieciowy z serwera do D365 (`https://<środowisko>.operations.dynamics.com`), do `login.microsoftonline.com` i do magazynu Azure Blob (pakiety danych).
- Dla folderów sieciowych (UNC) – konto usługi z dostępem do udziałów (konto domenowe albo gMSA).
- Dla dostępu z innych komputerów – certyfikat serwera (najlepiej z firmowego CA) z kluczem prywatnym w magazynie `LocalMachine\My`.

## Instalacja kreatorem

Uruchom `RIS5-Setup-<wersja>.exe` jako administrator. Kreator (polski lub angielski) prowadzi przez strony:

1. **Konto usługi**
   - *LocalService* (domyślnie) – wystarczy dla folderów lokalnych.
   - *NetworkService* / *LocalSystem* – konto komputera w sieci.
   - *Konto domenowe lub gMSA* – wymagane dla folderów sieciowych (UNC). Hasło jest sprawdzane od razu; gMSA (`DOMENA\konto$`) nie ma hasła. Instalator nadaje kontu prawo „Logowanie w trybie usługi”.
2. **Dostęp**
   - *Port HTTP* (domyślnie 5080) – działa tylko lokalnie na serwerze (`http://localhost:5080`).
   - *Port HTTPS* (np. 5443) i *odcisk certyfikatu* – dostęp z innych komputerów. Instalator sprawdza certyfikat (istnieje, ma klucz prywatny, jest ważny), nadaje kontu usługi dostęp do klucza i dodaje regułę zapory „RIS5 HTTPS”.
   - *Administratorzy* i *Operatorzy* – grupy lub użytkownicy rozdzieleni `;` (np. `DOMENA\RIS-Admins;DOMENA\jan.kowalski`). Domyślnie lokalni Administratorzy i instalujący użytkownik.
3. **Import z 4.x** – pojawia się, gdy na serwerze działa RIS 4.x. Zadania są importowane jako **wstrzymane**, razem z sekretami – patrz [Przejście z RIS 4.x](#upgrade4x).

Na koniec instalator zapisuje ustawienia, tworzy usługę **RIS5** (start automatyczny opóźniony, restart po awarii), uruchamia ją i sprawdza `/health`. Ostatnia strona pokazuje wynik i adres interfejsu.

> Program trafia do `%ProgramFiles%\RIS5` (linia poleceń `ris.exe` w `cli\`), dane do `%ProgramData%\RIS5`: bazy, klucze sekretów, logi, kopie zapasowe i plik ustawień `appsettings.json`.

## Instalacja cicha (skrypt, GPO)

```powershell
& .\RIS5-Setup-5.0.0.exe /VERYSILENT /SUPPRESSMSGBOXES /LOG=C:\Temp\ris5-setup.log `
  "/ACCOUNT=CONTOSO\svc-ris$" /HTTPPORT=5080 /HTTPSPORT=5443 "/CERT=<odcisk>" `
  "/ADMINS=CONTOSO\RIS-Admins" "/OPERATORS=CONTOSO\RIS-Operators" /IMPORT4X=0 /TASKS="cliPath"
```

| Parametr | Znaczenie |
|---|---|
| `/ACCOUNT` | `LocalSystem`, `LocalService`, `NetworkService`, `DOMENA\użytkownik` (z `/PASSWORD=`), `DOMENA\gmsa$` |
| `/HTTPPORT`, `/HTTPSPORT`, `/CERT` | Porty i odcisk certyfikatu HTTPS (bez HTTPS: pomiń `/HTTPSPORT` i `/CERT`) |
| `/ADMINS`, `/OPERATORS` | Role, wartości rozdzielone `;` – w PowerShellu ujmij cały argument w cudzysłów |
| `/IMPORT4X=0/1` | Import zadań z 4.x jako wstrzymanych |
| `/RECONFIGURE=1` | Przy aktualizacji: zapisz ustawienia na nowo (inaczej zostają dotychczasowe) |
| `/TASKS="cliPath"` | Dodaj `ris.exe` do zmiennej PATH |

Błędy walidacji (konto, port, certyfikat) kończą instalację cichą z błędem – szczegóły są w pliku `/LOG`.

## HTTPS i certyfikat

Do produkcji użyj certyfikatu z firmowego CA z nazwą serwera (i jego FQDN) w polu SAN, zaimportowanego z kluczem prywatnym do `LocalMachine\My`. Do testów wystarczy certyfikat samopodpisany (przeglądarka pokaże ostrzeżenie):

```powershell
$c = New-SelfSignedCertificate -DnsName $env:COMPUTERNAME, localhost -CertStoreLocation Cert:\LocalMachine\My -NotAfter (Get-Date).AddYears(1)
& .\RIS5-Setup-5.0.0.exe /VERYSILENT /SUPPRESSMSGBOXES /RECONFIGURE=1 /HTTPPORT=5080 /HTTPSPORT=5443 "/CERT=$($c.Thumbprint)"
```

Sprawdzenie: `https://<serwer>:5443/health` odpowiada `Healthy`. Po odnowieniu certyfikatu uruchom instalator ponownie z `/RECONFIGURE=1` i nowym odciskiem.

## Konto domenowe lub gMSA

Potrzebne, gdy zadania czytają lub zapisują pliki na udziałach sieciowych.

- **gMSA** (zalecane – bez hasła): administratorzy AD tworzą konto i zezwalają serwerowi na pobieranie hasła; na serwerze `Install-ADServiceAccount svc-ris$` i `Test-ADServiceAccount svc-ris$` (musi zwrócić `True`), potem `/ACCOUNT="CONTOSO\svc-ris$"`.
- **Zwykłe konto domenowe**: podaj je w kreatorze (hasło jest sprawdzane).
- Uprawnienia do udziałów (udostępnianie i NTFS) nadają administratorzy serwera plików. Dostęp sprawdzisz przyciskiem **Sprawdź dostęp** przy polach folderów w edytorze zadania – test wykonuje się kontem usługi.

## Role i uprawnienia {#roles}

| Rola | Może |
|---|---|
| **Admin** | Wszystko: zadania, rejestracje aplikacji, instancje D365, serwery SFTP, dostęp, audyt, utrzymanie danych. |
| **Operator** | Uruchamiać, wstrzymywać i wznawiać zadania (także zbiorczo) oraz wszystko, co Viewer. |
| **Viewer** | Tylko podgląd: pulpit, zadania, harmonogram, historia, szczegóły uruchomień, eksporty. |

**Po instalacji role nadaje się na stronie *Dostęp*** (rola Admin): wybierz rolę, wpisz grupę lub użytkownika (`DOMENA\RIS-Operatorzy`, `DOMENA\jan.kowalski` albo SID) i kliknij **Dodaj**. RIS sprawdza konto w Windows/AD, zmiana działa od następnego odświeżenia strony – bez restartu usługi – i trafia do audytu. Najwygodniej nadawać role **grupom AD**, a dostępem zarządzać przez członkostwo w grupach. Szczegóły: [Dostęp](06-daily-operations.md#access).

> Dodanie użytkownika do grupy AD zadziała po jego **ponownym zalogowaniu do Windows** – lista grup jest odczytywana przy logowaniu.

**Grupy lokalne serwera** – gdy nie możesz tworzyć grup w AD albo chcesz nadać dostęp tylko na tym serwerze, utwórz grupę lokalną i dodaj do niej konta domenowe (PowerShell jako administrator):

```powershell
New-LocalGroup -Name RIS-Operatorzy -Description "Operatorzy RIS 5"
Add-LocalGroupMember -Group RIS-Operatorzy -Member CONTOSO\jan.kowalski, CONTOSO\Księgowość
```

Na stronie *Dostęp* zacznij pisać w polu *Grupa lub użytkownik* – pole podpowiada grupy lokalne serwera. Można wpisać `SERWER\RIS-Operatorzy`, `.\RIS-Operatorzy` albo samą nazwę. Zmiana członkostwa w grupie lokalnej działa po ponownym otwarciu przeglądarki (bez wylogowania z Windows). Przy wielu serwerach RIS wygodniejsze są grupy AD – jedna grupa dla wszystkich serwerów.

**Plik ustawień.** Role podane w instalatorze (`/ADMINS`, `/OPERATORS`) są w `%ProgramData%\RIS5\appsettings.json` i na stronie *Dostęp* są tylko do odczytu (ikona kłódki) – dzięki temu administratorzy nie mogą przypadkiem odciąć sobie dostępu. Zmiana w pliku wymaga restartu usługi:

```json
{
  "Ris": {
    "Security": {
      "Roles": {
        "Admin": [ "S-1-5-32-544", "CONTOSO\\RIS-Admins" ],
        "Operator": [ "CONTOSO\\RIS-Operators" ]
      }
    }
  }
}
```

Można podawać grupy, użytkowników albo SID-y (`S-1-5-32-544` = lokalni Administratorzy w każdej wersji językowej Windows).

**Linia poleceń** (na serwerze, jako administrator – do skryptów albo gdy nikt nie ma już roli Admin):

```powershell
ris access list
ris access add Operator "CONTOSO\RIS-Operators"
ris access remove Operator "CONTOSO\RIS-Operators"
```

> **Uwaga (UAC):** w przeglądarce uruchomionej na samym serwerze grupa Administratorzy jest w tokenie „tylko do odmowy”. Dlatego instalator dopisuje do administratorów konto instalującego użytkownika. Jeśli widzisz stronę „Brak dostępu”, dodaj swoje konto po nazwie.

## Aktualizacja

Uruchom instalator nowej wersji. Zatrzymuje on usługę (trwające zadania są przerywane w kilka sekund, ich Execution Id trafia do logu), kopiuje `ris.db` i `quartz.db` do `backup\before-upgrade-<data>`, podmienia program, zachowuje ustawienia i konto usługi, a potem uruchamia usługę. Zmiany schematu bazy wykonują się przy starcie (z kopią zapasową).

## Deinstalacja

*Ustawienia → Aplikacje → Recurring Integrations Scheduler 5*. Usługa i reguła zapory są usuwane. Dane w `%ProgramData%\RIS5` zostają, chyba że potwierdzisz ich usunięcie (deinstalacja cicha nigdy nie usuwa danych).

## Przejście z RIS 4.x {#upgrade4x}

RIS 5 instaluje się **obok** RIS 4.x na tym samym serwerze (osobna usługa, osobne dane i porty), a zadania przełącza się pojedynczo.

**Przed instalacją**

1. W kliencie RIS 4.x **zapisz harmonogram** (*Save schedule*). RIS 4.x trzyma zmiany wprowadzone w kliencie tylko w pamięci usługi – plik na dysku może być nieaktualny.
2. Sprawdź, na jakim koncie działa usługa RIS 4.x – instalator podpowie to samo konto, bo ma ono dostęp do folderów integracji.

**Instalacja**

3. Uruchom instalator RIS 5 jako administrator na serwerze RIS 4.x. Instalator sam znajduje harmonogram 4.x (przez usługę 4.x i jej plik konfiguracji, więc także w niestandardowym folderze) i pokazuje stronę **Import z RIS 4.x**.
4. Zadania są importowane jako **wstrzymane**. **Sekrety** 4.x (client secret, hasła SFTP) są odszyfrowywane na tym serwerze i zapisywane w RIS 5 – nie trafiają do żadnego pliku. Raport importu: `%ProgramData%\RIS5\import-4x\import.log` (lista ostrzeżeń i sekretów, których nie dało się przenieść) oraz `jobs.json` (konfiguracja bez sekretów).
   RIS 4.x zapisywał sekret w **każdym zadaniu osobno** – jeśli sekret aplikacji był odnawiany tylko w części zadań, zadania mają różne sekrety. Import bierze też sekrety z rejestracji aplikacji klienta 4.x (*Parameters → Azure App registrations*, plik `portable.config` obok `Schedule.xml` – tym sekretem klient 4.x wykonuje *Validate*), sprawdza wszystkie w Entra ID i zapisuje ten, który działa (raport podaje, z którego zadania pochodzi albo że żaden nie został przyjęty).
   Każda aplikacja 4.x staje się jedną **rejestracją aplikacji** (z nazwą z klienta 4.x, np. `MCOV-TEST-02`, a bez `portable.config` – `APP-` i początek client id), a każdy adres AOS jedną **instancją D365**, której domyślną rejestracją jest ta używana przez większość jej zadań. Zadanie, które w 4.x logowało się inną aplikacją, dostaje ją jako własną rejestrację aplikacji.
5. Instalacja cicha: `/IMPORT4X=1` (domyślnie). Dla konta domenowego podaj `/ACCOUNT` i `/PASSWORD` – bez nich użyte zostanie konto usługi 4.x, ale zwykłe konto domenowe wymaga hasła.

**Sprawdzenie po instalacji**

6. *Rejestracje aplikacji* i *Instancje D365* – **Testuj wszystkie**. Brakujące sekrety (wymienione w raporcie) wpisz ręcznie w rejestracji aplikacji – raz dla wszystkich instancji i zadań.
7. *Zadania* (filtr *Wstrzymane*) – przejrzyj zadania. **Godzina startu**: RIS 4.x nie zapisywał godziny startu harmonogramów „co N minut/godzin”, więc RIS 5 przyjmuje 00:00 – popraw zadania, które miały ruszać o innej porze (można zbiorczo: *Edycja zbiorcza → Harmonogram*).
8. Przy folderach i szablonach pakietów użyj **Sprawdź dostęp** – test wykonuje się kontem usługi RIS 5.

**Przełączanie zadań**

9. Dla każdego zadania (zadanie i jego **monitor razem**): zatrzymaj je w RIS 4.x → wznów w RIS 5 (*Wznów*) → po pierwszym uruchomieniu sprawdź historię. Monitor RIS 5 czyta pliki statusu zostawione przez import 4.x, więc nic, co jest w trakcie, nie zginie.
10. **Uwaga:** wstrzymanie w RIS 4.x nie przetrwa restartu usługi 4.x (np. po łatkach Windows) – zadanie ruszy wtedy znowu i będzie działać równolegle z RIS 5. Po przełączeniu **usuń zadanie w RIS 4.x i zapisz harmonogram**, a po przełączeniu wszystkich zadań wyłącz usługę RIS 4.x (`Set-Service RecurringIntegrationsScheduler -StartupType Disabled`).

Ręcznie (np. na innym serwerze): `ris db import-4x Schedule.xml --paused` (sekrety przenoszą się tylko na serwerze, na którym RIS 4.x je zaszyfrował; gdzie indziej ustaw je w *Rejestracjach aplikacji*).
