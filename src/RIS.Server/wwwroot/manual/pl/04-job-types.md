# Typy zadań

| Typ | Kierunek | Co robi | API D365 |
|---|---|---|---|
| **Eksport** | D365 → folder | Uruchamia eksport projektu danych, czeka na zakończenie, pobiera pakiet (opcjonalnie rozpakowuje, dodaje znacznik czasu, wysyła na SFTP). | Package API (`ExportToPackage`) |
| **Import** | folder → D365 | Pakuje pliki (albo bierze gotowe pakiety) i importuje je do projektu danych. Zapisuje plik statusu dla monitora. | Package API (`ImportFromPackage`) |
| **Monitor wykonania** | – | Sprawdza w D365 wynik importów i przenosi pliki do folderu przetworzonych albo błędów (z kluczami błędów i linkiem do D365). | Package API |
| **Wysyłka (Upload)** | folder → D365 | Wysyła pliki do zadania cyklicznego D365 (kolejka). Zapisuje plik statusu dla monitora. | Recurring Integrations (`enqueue`) |
| **Monitor przetwarzania** | – | Sprawdza, czy D365 przetworzył wysłane komunikaty; przenosi pliki jak monitor wykonania. | Recurring Integrations |
| **Pobieranie (Download)** | D365 → folder | Pobiera pakiety wystawione przez zadanie cykliczne eksportu D365 (`dequeue`), zapisuje je i potwierdza odbiór (`ack`). | Recurring Integrations |

## Którego typu użyć?

- **Eksport danych z D365 o stałej porze** (raporty, interfejsy do innych systemów) → **Eksport**. RIS sam zleca eksport i pobiera wynik.
- **Import plików do D365 z kontrolą wyniku** → **Import + Monitor wykonania** (kreator tworzy parę jednym przełącznikiem).
- **D365 już ma zdefiniowane zadanie cykliczne** (recurring data job) → **Wysyłka + Monitor przetwarzania** (import) albo **Pobieranie** (eksport). Harmonogram przetwarzania jest wtedy w D365, a RIS tylko dostarcza lub odbiera pliki.

## Foldery i pliki statusu

Zadania plikowe pracują na folderach – przykład pary Import + monitor:

```
D:\Integrations\VendorInvoices\
  In\                 ← tu system źródłowy zapisuje pliki (InputDir)
  Uploaded\           ← plik wysłany do D365 + plik .Status dla monitora (UploadSuccessDir)
  UploadErrors\       ← D365 odrzucił plik przy wysyłce (+ raport błędu .Status)
  Processed\          ← monitor: import zakończony sukcesem (ProcessingSuccessDir)
  ProcessingErrors\   ← monitor: import z błędami (+ klucze błędów, log wykonania, link .url do D365)
```

- **Plik statusu** (`.Status`, JSON) łączy zadanie importu lub wysyłki z monitorem: zawiera Execution Id lub Message Id i czas zgłoszenia. Jest zgodny z RIS 4.x.
- Podfoldery w folderze wejściowym są zachowywane w folderach docelowych.
- Błąd jednego pliku nie zatrzymuje pozostałych – każdy plik jest przetwarzany i raportowany osobno.
- Ścieżki mogą być lokalne (`D:\…`) albo sieciowe (`\\serwer\udział\…` – wymaga konta domenowego lub gMSA).

## Pary zadań (zadanie + monitor)

Zadanie importu lub wysyłki i jego monitor wskazują się nawzajem (*Zadanie powiązane*). Monitor uruchamia się częściej (np. co 5 minut) i pracuje na folderze *Uploaded* zadania nadrzędnego. Kreator ustawia to automatycznie – patrz [Konfiguracja zadań](05-configuring-jobs.md).
