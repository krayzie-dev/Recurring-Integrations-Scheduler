# Obsługa codzienna

## Pulpit

![Pulpit](img/dashboard.png)

- **Kafelki** – liczba zadań: OK, z błędami, wstrzymane, w trakcie.
- **Wymaga uwagi** – zadania z błędami (z kodem i liczbą błędów z rzędu), zadania bez sukcesu od dawna, wstrzymane (z powodem i osobą).
- **Teraz działa** – uruchomione zadania z czasem trwania.
- **Uruchomienia w ostatnich 24 h** – sukcesy, błędy i pozostałe na godzinę.
- **Błędy z ostatnich 24 h** – kliknięcie otwiera szczegóły uruchomienia.

Strony odświeżają się same, gdy zadanie startuje lub się kończy.

## Lista zadań

![Lista zadań](img/jobs.png)

- **Szukaj** przeszukuje nazwę, opis, instancję D365, podsumowanie i harmonogram. Filtry: grupa, typ, instancja D365, szybkie filtry *Z błędami*, *Wstrzymane*, *W trakcie*.
- Kolorowy pasek przy typie oznacza typ zadania (te same kolory co ramki pasków na wykresach).
- **Akcje zbiorcze** po zaznaczeniu: *Uruchom teraz*, *Wstrzymaj* (z powodem widocznym dla wszystkich), *Wznów*, *Edycja zbiorcza*, *Usuń*.
- Ikona pobierania eksportuje widoczne lub zaznaczone zadania do Excela, CSV albo JSON (definicje bez sekretów).

## Strona zadania

![Przegląd zadania](img/job-overview.png)

Zakładki: **Przegląd** (stan, ostatnie i następne uruchomienia, harmonogram słownie), **Historia** (tabela uruchomień, *Pokaż starsze uruchomienia*), **Definicja** (JSON). Przyciski: *Uruchom teraz*, *Wstrzymaj / Wznów*, *Edytuj*, *Duplikuj*, *Usuń*.

![Historia zadania](img/job-history.png)

## Szczegóły uruchomienia

Kliknięcie uruchomienia (w historii, na wykresie, na pulpicie) otwiera panel ze szczegółami: wynik, czas, podsumowanie, **metryki** (Execution Id, plik, liczba plików, czas oczekiwania…) i **log przebiegu**. Ikona ↗ otwiera stronę uruchomienia z adresem do udostępnienia, ⟲ uruchamia zadanie ponownie.

![Szczegóły uruchomienia](img/run-details.png)

## Harmonogram (plan)

![Plan tygodnia](img/plan.png)

Wykres Gantta zaplanowanych uruchomień na dzień lub tydzień:

- **Wypełnienie paska** – stan (zaplanowane, wstrzymane – przerywane), **ramka** – typ zadania (legenda pod paskiem narzędzi).
- Zadania uruchamiane częściej niż co 30 minut są pokazane jako **pas** („co 5 min”).
- Długość paska to mediana czasu trwania ostatnich uruchomień.
- Grupowanie: grupa, instancja D365, typ. Linia czerwona = teraz. Kliknięcie paska otwiera zadanie.
- Eksport: PNG, PDF (wydruk), Excel, CSV.

## Historia uruchomień

![Historia tygodnia](img/history.png)

Ten sam wykres dla rzeczywistych uruchomień: kolor = wynik (sukces, błąd, przerwane, w trakcie), ▲ = przegapiony termin, ramka = typ. Uruchomienia, które przy danej skali nachodzą na siebie, są łączone w jeden pasek z liczbą („12 uruchomień”). Pod wykresem tabela uruchomień ze stronicowaniem; przełącznik *Tylko błędy*. Nad tabelą są **filtry**: tekst (zadanie, podsumowanie, treść błędu, identyfikator uruchomienia), grupa, wynik, kod błędu i *Uruchomione ręcznie*. Licznik pokazuje, ile uruchomień pasuje („Pokazano 28 z 291”), a eksport do Excela i CSV obejmuje tylko przefiltrowane uruchomienia. Te same filtry są w zakładce *Historia* na stronie zadania.

![Filtry listy uruchomień: tekst „Sales” i wynik „Błąd”](img/history-filter.png)

## Audyt

*Audyt* (rola Admin) zapisuje każdą zmianę konfiguracji: kto, kiedy, co. Po rozwinięciu wpisu widać **zmienione pola** (przed – przekreślone, po – wyróżnione); przełączniki *Wszystkie pola* i *JSON*. Sekrety nigdy nie są zapisywane – zmiana sekretu jest widoczna jako `*** (nowa wartość, ukryta)`.

![Audyt z różnicami](img/audit.png)

## Eksporty

| Co | Formaty | Gdzie |
|---|---|---|
| Lista zadań | Excel, CSV, JSON definicji | Zadania |
| Plan | PNG, PDF, Excel, CSV | Harmonogram |
| Historia | PNG, PDF, Excel, CSV | Historia, strona zadania |
| Audyt | Excel, CSV | Audyt |

CSV jest w UTF-8 z separatorem zgodnym z językiem (`;` po polsku), więc otwiera się poprawnie w Excelu.
