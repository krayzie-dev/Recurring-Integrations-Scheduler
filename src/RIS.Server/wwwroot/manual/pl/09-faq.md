# FAQ – najczęstsze pytania

### Czy RIS 5 może działać równolegle z RIS 4.x?
Tak – to osobna usługa (`RIS5`) z osobnymi danymi. Importuj zadania 4.x jako wstrzymane i przełączaj je kolejno: wstrzymaj w 4.x, wznów w RIS 5. Nie uruchamiaj tego samego zadania w obu wersjach jednocześnie.

### Gdzie są przechowywane sekrety i kto je widzi?
Zaszyfrowane w `ris.db` kluczami z `%ProgramData%\RIS5\keys`. Nie są wyświetlane w interfejsie, zwracane przez API ani zapisywane w audycie i logach. Można je tylko nadpisać.

### Jak zmienić sekret, który wygasa?
*Instancje D365 → edycja instancji → wklej nową wartość w polu Client secret → Testuj połączenie → Zapisz*. Zadania korzystają z nowego sekretu od następnego uruchomienia. W audycie pojawi się wpis `*** (nowa wartość, ukryta)`.

### Co się stanie, gdy serwer zostanie wyłączony w czasie zaplanowanego uruchomienia?
Po starcie usługa wykryje przegapiony termin: przy ustawieniu *Uruchom raz po starcie* uruchomi zadanie jeden raz, przy *Pomiń* poczeka na następny termin. Rytm harmonogramu nie przesuwa się.

### Zadanie uruchamia się co 2 godziny od 12:00 – czy po restarcie liczy od nowa?
Nie. Rytm wynika z harmonogramu (12:00, 14:00…), nie z chwili startu usługi.

### Jak uruchamiać zadanie tylko w godzinach pracy?
Harmonogram *Co N minut lub godzin* → przełącznik *Tylko w godzinach* (np. 08:00–16:00) i wybrane dni tygodnia. Koniec okna jest włącznie.

### Jak zaimportować jednym pakietem kilka encji (np. nagłówki i wiersze)?
Projekt danych w D365 z tymi encjami, w RIS koperta *Generowana*. System źródłowy zapisuje do folderu wejściowego **ZIP** z plikami nazwanymi jak encje (`Sales order headers V2.csv`, `Sales order lines V2.csv`). RIS ułoży je w kolejności z projektu. Przycisk *Pokaż encje projektu* podpowie nazwy plików.

### Czy potrzebuję szablonu pakietu jak w 4.x?
Nie, chyba że projekt danych ma w D365 własne mapowanie, transformacje lub wartości domyślne. Wtedy wybierz *Z szablonu pakietu (4.x)*.

### Dlaczego po imporcie RIS zadanie cykliczne D365 zaczęło źle wczytywać dane?
Import z *Nadpisz projekt danych* zastępuje mapowanie projektu. Używaj osobnych projektów danych dla zadań RIS i dla zadań cyklicznych.

### Jak sprawdzić, czy konto usługi ma dostęp do folderu sieciowego?
W edytorze zadania przy polu folderu **Sprawdź dostęp** – test tworzy i usuwa plik próbny kontem usługi i pokazuje nazwę konta.

### Jak wstrzymać wszystkie zadania na czas prac serwisowych D365?
Lista zadań → zaznacz wszystkie (lub filtr po instancji D365) → *Wstrzymaj* z powodem → po pracach *Wznów*. Wstrzymanie przetrwa restart usługi.

### Jak długo przechowywana jest historia uruchomień?
Domyślnie 30 dni (nieudane – 90 dni), maksymalnie 1000 uruchomień na zadanie. Zmienisz to w `Ris:Retention` – patrz [Utrzymanie](07-maintenance.md).

### Jak uzyskać dostęp z innego komputera?
Włącz HTTPS w instalatorze (`/RECONFIGURE=1 /HTTPSPORT=5443 /CERT=<odcisk>`) i otwórz `https://<serwer>:5443`. HTTP działa tylko lokalnie na serwerze.

### Jak dodać nowego operatora?
Dopisz użytkownika lub (lepiej) grupę AD w `%ProgramData%\RIS5\appsettings.json` w `Ris:Security:Roles:Operator` i uruchom usługę ponownie. Najprościej zarządzać rolami przez członkostwo w grupach AD.

### Czy mogę zautomatyzować obsługę (skrypty, CI)?
Tak – REST API `/api/v1` (logowanie Windows, te same role): lista zadań, uruchomienie, wstrzymanie, historia, eksport konfiguracji. Opis: `/openapi/v1.json` na serwerze.

### Strona pokazuje „Utracono połączenie z serwerem RIS”.
Usługa była restartowana lub aktualizowana. Po jej starcie strona przeładuje się sama; jeśli nie – odśwież ją (F5).

### Wpisy dziennika zdarzeń są w logu Application, a nie RIS5.
Po aktualizacji ze starszej wersji Windows stosuje przeniesienie źródła do logu RIS5 dopiero po restarcie komputera.

### Gdzie zgłaszać błędy?
Z kodem błędu, Execution Id i fragmentem logu zadania (`logs\jobs\<zadanie>\`) – sekrety nigdy nie trafiają do logów, więc można je przekazywać.
