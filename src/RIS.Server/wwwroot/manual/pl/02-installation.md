# Instalacja

## Wymagania

- Windows Server 2019 lub nowszy (lub Windows 10/11 do testów), 64-bit. **.NET nie trzeba instalować** – program zawiera własne środowisko.
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
3. **Import z 4.x** – pojawia się, gdy na serwerze jest `Schedule.xml` z RIS 4.x. Zadania są importowane jako **wstrzymane** (sekretów nie importuje się – ustawisz je w połączeniach).

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

Role są w pliku `%ProgramData%\RIS5\appsettings.json` (zapisuje go instalator):

```json
{
  "Ris": {
    "Security": {
      "Roles": {
        "Admin": [ "S-1-5-32-544", "CONTOSO\\RIS-Admins" ],
        "Operator": [ "CONTOSO\\RIS-Operators" ],
        "Viewer": [ "CONTOSO\\Domain Users" ]
      }
    }
  }
}
```

Można podawać grupy, użytkowników albo SID-y (`S-1-5-32-544` = lokalni Administratorzy w każdej wersji językowej Windows). Po zmianie uruchom usługę ponownie.

> **Uwaga (UAC):** w przeglądarce uruchomionej na samym serwerze grupa Administratorzy jest w tokenie „tylko do odmowy”. Dlatego instalator dopisuje do administratorów konto instalującego użytkownika. Jeśli widzisz stronę „Brak dostępu”, dopisz swoje konto po nazwie.

## Aktualizacja

Uruchom instalator nowej wersji. Zatrzymuje on usługę (trwające zadania są przerywane w kilka sekund, ich Execution Id trafia do logu), kopiuje `ris.db` i `quartz.db` do `backup\before-upgrade-<data>`, podmienia program, zachowuje ustawienia i konto usługi, a potem uruchamia usługę. Zmiany schematu bazy wykonują się przy starcie (z kopią zapasową).

## Deinstalacja

*Ustawienia → Aplikacje → Recurring Integrations Scheduler 5*. Usługa i reguła zapory są usuwane. Dane w `%ProgramData%\RIS5` zostają, chyba że potwierdzisz ich usunięcie (deinstalacja cicha nigdy nie usuwa danych).

## Przejście z RIS 4.x

1. Zainstaluj RIS 5 obok 4.x z opcją **Import z 4.x** (lub później: `ris convert-schedule Schedule.xml -o jobs.json` i `ris db import jobs.json --paused`).
2. Uzupełnij sekrety w **Połączenia** (sekrety 4.x nie są przenoszone).
3. Uruchamiaj pojedyncze zadania przyciskiem **Uruchom teraz** na środowisku testowym i porównuj wyniki.
4. Wstrzymaj zadania w 4.x i wznów je w RIS 5 (zbiorczo na liście zadań).
