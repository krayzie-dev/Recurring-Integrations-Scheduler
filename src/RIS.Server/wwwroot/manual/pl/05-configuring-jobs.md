# Konfiguracja zadań

## Kreator zadania

*Zadania → Nowe zadanie* (rola Admin). Nowe zadanie tworzy się krok po kroku; przy edycji istniejącego te same kroki są zakładkami, między którymi można przechodzić dowolnie.

![Krok 1 – wybór typu zadania](img/wizard-type.png)

| Krok | Zawartość |
|---|---|
| **1. Zadanie** | Typ (karta z opisem i schematem przepływu), nazwa, grupa, opis. Przy Imporcie i Wysyłce przełącznik *Utwórz też zadanie monitorujące*. Lista *Na podstawie istniejącego zadania* kopiuje wszystkie ustawienia. |
| **2. Dynamics 365** | Instancja D365, rejestracja aplikacji (puste = domyślna instancji; inna, gdy zadanie ma działać jako inna aplikacja – jak w 4.x), projekt danych lub activity ID, firma, opcje importu. Przyciski **Testuj połączenie** i **Pokaż encje projektu**. |
| **3. Pliki i foldery** | Foldery wejścia, sukcesu i błędów, wybór i kolejność plików, rozpakowanie, SFTP. Przy folderach przycisk **Sprawdź dostęp** (test kontem usługi: istnienie, odczyt, zapis). |
| **4. Harmonogram** | Konstruktor harmonogramu, strefa czasowa, przegapione terminy, wstrzymanie – patrz [Harmonogram](#schedule). |
| **5. Zaawansowane** | Odpytywanie statusu (polling), ponawianie żądań, wstrzymanie po błędzie, ścieżki akcji OData. |
| **6. Podsumowanie** | Opis słowny całego zadania, najbliższe uruchomienia i walidacja całej konfiguracji przed zapisem. |

Panel **pomocy po prawej** opisuje bieżący krok i pole oznaczone ikoną ⓘ oraz pokazuje problemy kroku (np. brak wymaganego folderu). Przycisk **Dalej** nie pozwoli przejść dalej, dopóki krok ma błędy.

Zadanie można też **powielić** (przycisk *Duplikuj* na stronie zadania) – kopia otwiera się w kreatorze z dopiskiem „(kopia)” w nazwie.

## Przykład 1: Eksport

Cel: codziennie o 02:00 wyeksportować faktury sprzedaży (projekt danych `CustInvoiceJour`) z firmy USMF do `D:\Integrations\Invoices\Out` i rozpakować pakiet.

1. **Typ**: Eksport, nazwa `CustInvoiceJour`, grupa `Export jobs`.
2. **Dynamics 365**: instancja D365 `CONTOSO-PROD`, projekt danych `CustInvoiceJour` (dokładnie jak w D365), firma `USMF`.
3. **Pliki**: folder pobranych `D:\Integrations\Invoices\Out`, zaznacz *Rozpakuj pakiet*. Opcjonalnie *Dodaj znacznik czasu*, *Usuń pakiet po rozpakowaniu*, wysyłka na SFTP.
4. **Harmonogram**: *Codziennie* o `02:00`.
5. **Podsumowanie → Zapisz**.

Po każdym uruchomieniu w historii widać Execution Id, status D365, liczbę sprawdzeń statusu i nazwę pliku. Eksport czeka na wynik do 12 godzin (rosnące odstępy między sprawdzeniami) – ustawienie w kroku *Zaawansowane*.

## Przykład 2: Import z monitorem wykonania

Cel: pliki faktur zakupowych pojawiające się w `D:\Integrations\VendorInvoices\In` importować co 15 minut w dni robocze 7–18 i sprawdzać wynik.

1. **Typ**: Import, nazwa `Vendor invoices import`, grupa `Finance`, zaznacz **Utwórz też zadanie monitorujące**.
2. **Dynamics 365**: instancja D365, projekt danych `Vendor invoices`, firma `USMF`, **Koperta pakietu: Generowana z projektu danych**, **Nadpisz projekt danych** włączone.

   ![Krok Dynamics 365 zadania importu](img/editor-d365.png)

3. **Pliki**: `In`, `Uploaded`, `UploadErrors`; dla monitora `Processed` i `ProcessingErrors`.

   ![Krok Pliki i foldery](img/editor-files.png)

4. **Harmonogram**: *Co 15 minut*, przełącznik *Tylko w godzinach* 07:00–18:00, dni Pn–Pt. Monitor dostaje harmonogram co 5 minut.

### Koperta pakietu

D365 importuje **pakiety danych** (ZIP z plikami danych, `Manifest.xml` i `PackageHeader.xml`). RIS buduje pakiet na dwa sposoby:

- **Generowana z projektu danych (zalecane)** – RIS odczytuje z D365 encje projektu (kolejność, format, ustawienia walidacji) i sam tworzy `Manifest.xml` i `PackageHeader.xml`. Szablon nie jest potrzebny. Kolumny są mapowane **po nazwach** (dowolna kolejność, można podać tylko część kolumn).
  - **Pojedynczy plik** trafia do jedynej encji projektu – nazwa pliku jest dowolna.
  - **Plik ZIP z plikami kilku encji** (bez manifestu) staje się jednym pakietem. Pliki nazwij jak encje, np. `Vendor invoice header.csv` i `Vendor invoice lines.csv` (wielkość liter, spacje, `_` i `-` nie mają znaczenia). Kolejność wykonania pochodzi z projektu w D365.
  - **ZIP z `Manifest.xml`** jest wysyłany bez zmian (gotowy pakiet).
  - Jeśli pliki nie pasują do encji (brak pliku dla encji, plik bez encji, dwa pliki dla jednej encji), plik trafia do błędów z kodem `RIS-IMP-004` i nic nie jest wysyłane do D365. **D365 sam nie zgłasza brakujących plików** – zaimportowałby resztę i raportował sukces. Opcja *Dopuszczaj niepełne pakiety* wyłącza tę kontrolę.
  - Przycisk **Pokaż encje projektu** pokazuje encje, poziomy i oczekiwane nazwy plików.
- **Z szablonu pakietu (4.x)** – kopia pliku ZIP wyeksportowanego z D365, do którego RIS wkłada plik danych. Potrzebne, gdy projekt ma w D365 własne mapowanie, transformacje lub wartości domyślne.

> **Nadpisz projekt danych** jest wymagane, gdy projekt istnieje już w D365 (D365 odrzuca wtedy import bez tej opcji: *„The data project … exists and overwrite has not been specified”*). Nadpisanie zastępuje **mapowanie pól projektu** mapowaniem z pakietu (przy kopercie generowanej – kolumnami importowanego pliku). Dlatego **importy RIS powinny mieć własny projekt danych**, nieużywany przez zadania cykliczne ani ręczne importy.

### Import do wielu firm

Jedno zadanie może importować pliki do różnych firm. W kroku *Dynamics 365* zostaw pole firmy puste i ustaw sekcję **Wiele firm**:

- **Z podfolderu** – firma to nazwa folderu z plikiem: `In\2001\grupy.csv` → 2001, `In\2101\grupy.csv` → 2101. Podfoldery zostają zachowane w folderach docelowych.
- **Z nazwy pliku** – firma to fragment nazwy: przy separatorze `_` i pozycji 1 plik `2001_grupy.csv` → 2001. Ten wariant działa też z plikami pobieranymi z SFTP.

Przed wysłaniem RIS sprawdza, czy firma istnieje w D365. Plik z nieznaną firmą (np. `grupy.csv` bez kodu) trafia od razu do błędów z kodem `RIS-IMP-002` i nie jest wysyłany do D365; pozostałe pliki są importowane. Listę firm RIS odczytuje raz na dobę (dla każdego środowiska), a gdy trafi na nieznany kod – odświeża ją, najwyżej co 5 minut, więc firma założona w ciągu dnia zostanie rozpoznana.

## Przykład 3: Wysyłka (Upload) z monitorem przetwarzania {#upload}

Cel: zamówienia ze sklepu internetowego pobierane z SFTP i wysyłane do zadania cyklicznego D365 co 10 minut.

1. **W D365**: w projekcie danych importu *Utwórz zadanie cykliczne* (recurring data job): nazwa, **Application ID** = client id aplikacji RIS (zaznacz *Enabled*), typ źródła *File*, encja, harmonogram przetwarzania (np. co minutę), *Enabled*. Skopiuj **ID** zadania (GUID w nawiasach klamrowych) – to **activity ID**.
2. **W RIS**: typ Wysyłka, zaznacz *Utwórz też zadanie monitorujące*; activity ID, nazwa encji, firma; foldery; *Pobierz z SFTP* (serwer, folder zdalny, maska plików).

   ![Krok Dynamics 365 zadania wysyłki](img/editor-upload.png)

3. Harmonogram co 10 minut, monitor co 5 minut.

Monitor przetwarzania przenosi plik do *Processed*, gdy D365 przetworzy komunikat, albo do *ProcessingErrors* (z błędami wykonania), gdy przetwarzanie się nie powiedzie.

## Przykład 4: Pobieranie (Download)

1. **W D365**: w projekcie danych eksportu *Utwórz zadanie cykliczne* z **Application ID** aplikacji RIS i harmonogramem eksportu. Skopiuj jego ID (activity ID).
2. **W RIS**: typ Pobieranie, activity ID, folder pobranych, opcjonalnie rozpakowanie i znacznik czasu, harmonogram (np. `6, 12, 18` w dni robocze).

RIS pobiera wszystkie komunikaty z kolejki, zapisuje pakiety i **potwierdza odbiór (ack) każdego osobno** dopiero po zapisaniu pliku – nieudane pobranie nie gubi danych (D365 wystawi komunikat ponownie).

## Harmonogram {#schedule}

![Konstruktor harmonogramu z oknem godzinowym](img/editor-schedule.png)

| Tryb | Przykład | Wynik |
|---|---|---|
| **Codziennie / w wybrane dni** | 02:00, dni Pn–Pt | Od poniedziałku do piątku o 02:00 |
| **Co N minut lub godzin** | co 2 godziny od 12:00 | 00:00, 02:00 … 12:00, 14:00 … (rytm liczony od 12:00, restart go nie przesuwa) |
| **… tylko w godzinach** | co 10 minut, 08:00–16:00, Pn–Pt | 08:00, 08:10 … 15:50, 16:00 w dni robocze (koniec okna włącznie) |
| **Co miesiąc** | 1. dnia o 06:00 / ostatni dzień | |
| **Zaawansowany (cron)** | `0 0 8-18 ? * MON-FRI` | Wyrażenie Quartz (sekundy minuty godziny dzień miesiąc dzień-tygodnia) |

- **Strefa czasowa** – domyślnie strefa serwera; godziny pozostają stałe także przy zmianie czasu letniego/zimowego.
- **Przegapione terminy** – gdy usługa nie działała w zaplanowanym terminie: *Uruchom raz po starcie* (zalecane dla eksportów) albo *Pomiń*.
- **Wstrzymane** – zadanie jest zapisane, ale się nie uruchamia. To ten sam stan co *Wstrzymaj*/*Wznów* na liście zadań i *Wstrzymaj po błędzie*: przełącznik pokazuje, czy zadanie jest teraz wstrzymane, a zapis formularza zachowuje powód i osobę, która wstrzymała.
- Pod konstruktorem: opis słowny i tabela najbliższych uruchomień.

## Kopiowanie i edycja wielu zadań

Na liście zadań zaznacz zadania i wybierz **Edycja zbiorcza** (rola Admin):

- **Wspólne pola** – instancja D365, rejestracja aplikacji (ustaw albo wyczyść = domyślna instancji), grupa, opis, strefa czasowa, przegapione terminy, polling, ponawianie, wstrzymanie po błędzie,
- **Harmonogram** – przesunięcie o ±N minut, ten sam cron lub interwał, *rozłożenie startów* (kolejne zadania co N minut),
- **Zamiana w ścieżkach** – np. `\\stary-serwer\udział` → `\\nowy-serwer\udział` we wszystkich folderach.

Każda zmiana pokazuje **podgląd różnic** i jest walidowana w całości; zapis odbywa się w jednej transakcji (wszystko albo nic) i trafia do audytu.

![Zaznaczone zadania i akcje zbiorcze](img/bulk.png)
