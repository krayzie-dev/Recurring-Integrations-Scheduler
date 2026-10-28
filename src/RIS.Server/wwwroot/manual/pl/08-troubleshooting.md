# Rozwiązywanie problemów

## Gdzie szukać przyczyny

1. **Szczegóły uruchomienia** – podsumowanie z kodem błędu, metryki (Execution Id) i log przebiegu.
2. **Folder błędów** zadania – obok pliku leży raport `.Status` z komunikatem D365 i pełną odpowiedzią.
3. **Log zadania** – `%ProgramData%\RIS5\logs\jobs\<Grupa.Zadanie>\`.
4. **D365** – *Zarządzanie danymi → Historia zadań* (wyszukaj po Execution Id); dla zadań cyklicznych *Zarządzaj zadaniami cyklicznymi*.

## Typowe sytuacje

| Objaw | Przyczyna i rozwiązanie |
|---|---|
| `RIS-AUTH-001` … `AADSTS7000215: Invalid client secret` | Wklejono *Secret ID* zamiast **wartości** sekretu albo sekret wygasł. Utwórz nowy sekret, wklej kolumnę *Value* w instancji D365 i użyj **Testuj połączenie**. |
| `RIS-AUTH-001` … `AADSTS700016: Application … was not found` | Zły client id albo tenant w instancji D365. |
| `RIS-HTTP-001` 401/403 przy wywołaniu D365 | Aplikacji nie dodano w D365 (*Aplikacje Microsoft Entra ID*) albo jej użytkownik nie ma uprawnień do zarządzania danymi lub firmy. |
| `RIS-IMP-001` … *exists and overwrite has not been specified* | Projekt danych istnieje w D365 – włącz **Nadpisz projekt danych** w zadaniu. |
| Po imporcie RIS zadanie cykliczne D365 wczytuje dane do złych pól | Import z nadpisaniem zmienił mapowanie projektu. Użyj osobnych projektów danych dla RIS i dla zadań cyklicznych; mapowanie przywróci import pliku z pełnym zestawem kolumn. |
| `RIS-IMP-004` | Pliki pakietu nie pasują do encji projektu – komunikat wymienia brakujące encje i nieznane pliki. Nazwij pliki jak encje. |
| `RIS-IMP-002` … *does not exist in D365* | Import do wielu firm: kod firmy z nazwy pliku lub folderu nie istnieje w D365. Popraw nazwę pliku/folderu i przenieś plik z folderu błędów z powrotem do folderu wejściowego. |
| `RIS-IMP-003` | Projekt danych nie istnieje (sprawdź dokładną nazwę) albo nie ma włączonych encji. |
| `RIS-EXP-001` | Eksport nie zakończył się w limicie czasu (domyślnie 12 h). Sprawdź wykonanie w D365 po Execution Id i obciążenie serwera wsadowego. |
| `RIS-HTTP-002` (503, 504, DNS) | Chwilowa niedostępność D365 (okno serwisowe) albo problem sieci/DNS serwera. Zwykle mija sam; powtarzające się błędy DNS zgłoś administratorom sieci. |
| `RIS-SFTP-001` | Serwer SFTP niedostępny, złe dane logowania albo folder zdalny. Użyj **Testuj połączenie** w *Serwerach SFTP*. |
| `RIS-CFG-003` / *Sprawdź dostęp* nie przechodzi dla `\\serwer\…` | Konto usługi nie ma dostępu do udziału – konta wbudowane (LocalService) nie mają dostępu sieciowego. Użyj konta domenowego lub gMSA. |
| `RIS-CFG-001` przy starcie, „klucz nie do odczytania” | Brak lub inne `keys\` (np. po przeniesieniu bazy na inny serwer) – ustaw sekrety ponownie. |
| Strona „Brak dostępu” | Twoje konto nie ma roli. Dopisz je (lub jego grupę) w `Ris:Security:Roles` i uruchom usługę ponownie. Na samym serwerze grupa Administratorzy nie wystarcza (UAC). |
| Okno „Utracono połączenie z serwerem RIS” | Usługa była restartowana (np. aktualizacja). Strona przeładuje się sama po starcie usługi. |
| Instalator: „Certificate not found in LocalMachine\My” | Odcisk wpisany ze spacjami lub z innego magazynu – certyfikat musi być w *Komputer lokalny → Osobisty* i mieć klucz prywatny. |
| Wpisy dziennika zdarzeń są w logu *Application* | Po aktualizacji Windows przenosi źródło do logu RIS5 dopiero po restarcie komputera. |
| Instalator z PowerShella: `ParserError` przy `/VERYSILENT` | Dwa polecenia wklejone w jednej linii; uruchom je osobno. |

## Kody błędów

Każdy błąd zaczyna się od kodu, np. `[RIS-EXP-001]`. Ten sam kod jest w podsumowaniu uruchomienia, w logu i (jako identyfikator) w dzienniku zdarzeń.

| Kod | Znaczenie | Co zrobić |
|---|---|---|
| RIS-CFG-001 | Nieprawidłowa konfiguracja | Komunikat wymienia błędne ustawienia (`ris validate`). |
| RIS-CFG-002 | Zadanie wskazuje nieistniejącą instancję D365 | Dodaj instancję albo popraw zadanie. |
| RIS-CFG-003 | Folder nie istnieje albo brak dostępu | Sprawdź ścieżkę i uprawnienia konta usługi. |
| RIS-CFG-004 | Nieprawidłowy szablon pakietu | Wyeksportuj nowy szablon z D365 albo użyj koperty generowanej. |
| RIS-AUTH-001 | Logowanie do Entra ID nie powiodło się | Tenant, client id, sekret/certyfikat (ważność); kod AADSTS w komunikacie. |
| RIS-HTTP-001 | D365 odrzucił żądanie (4xx) | Powód z D365 jest w komunikacie (projekt, uprawnienia, firma). |
| RIS-HTTP-002 | Problem chwilowy (5xx, timeout, sieć) | Zwykle mija; przy powtórzeniach sprawdź sieć/DNS. |
| RIS-HTTP-003 | Nieoczekiwana odpowiedź D365 | Odpowiedź jest w logu; zgłoś, jeśli się powtarza. |
| RIS-EXP-001…004 | Eksport: limit czasu / błąd w D365 / brak pakietu / pobieranie | Sprawdź wykonanie w D365 po Execution Id; dla 004 – dostęp do Azure Blob. |
| RIS-IMP-001…004 | Import: żądanie odrzucone / brak firmy / brak projektu / pliki nie pasują do encji | Patrz tabela wyżej. |
| RIS-UPL-001 | Wysyłka do kolejki nie powiodła się | Raport błędu leży obok pliku w folderze błędów. |
| RIS-DWN-001/002 | Pobieranie / potwierdzenie nie powiodło się | Sprawdź zadanie cykliczne (activity ID) w D365; komunikat wróci do kolejki. |
| RIS-MON-001/002 | Import/przetwarzanie nie zakończyło się w czasie / zakończyło się błędami | Plik w *ProcessingErrors* z kluczami błędów i linkiem `.url` do D365. |
| RIS-FS-001/002 | Plik zablokowany / błąd operacji na pliku | Zablokowany plik czeka na następne uruchomienie. |
| RIS-SFTP-001 | Błąd SFTP | Serwer, dane logowania, folder zdalny. |
| RIS-GEN-001/002 | Błąd nieoczekiwany / nieznany status D365 | Szczegóły w logu – zgłoś. |
