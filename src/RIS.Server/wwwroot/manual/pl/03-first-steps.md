# Pierwsze kroki: połączenia

Zanim utworzysz zadanie, RIS musi umieć zalogować się do D365. Robi to **aplikacja Microsoft Entra ID** (dawniej Azure AD) – w imieniu tej aplikacji RIS wywołuje API D365.

## 1. Rejestracja aplikacji w Entra ID

W portalu Azure: *Microsoft Entra ID → Rejestracje aplikacji → Nowa rejestracja*.

1. Nazwa, np. `RIS5-PROD`, typ konta: *Tylko konta w tym katalogu*. Adres przekierowania nie jest potrzebny.
2. Zanotuj **Application (client) ID** i **Directory (tenant) ID** albo nazwę domeny tenanta (np. `contoso.onmicrosoft.com`).
3. Sposób logowania – wybierz jeden:
   - **Client secret**: *Certificates & secrets → New client secret*. Skopiuj kolumnę **Value** (widoczna tylko raz, tuż po utworzeniu). Uwaga: *Secret ID* to nie jest sekret – jego wklejenie kończy się błędem `AADSTS7000215`. Zanotuj datę wygaśnięcia.
   - **Certyfikat**: wgraj klucz publiczny certyfikatu (`.cer`) w *Certificates & secrets → Certificates*. Certyfikat z kluczem prywatnym zaimportuj na serwerze RIS do `LocalMachine\My`; konto usługi musi mieć dostęp do klucza.
4. Uprawnienia API nie są wymagane (D365 autoryzuje aplikację po swojej stronie).

## 2. Aplikacja w D365

W D365: *Administrowanie systemem → Konfiguracja → Aplikacje Microsoft Entra ID* (Microsoft Entra ID applications) → **Nowy**:

- **Identyfikator klienta** – Application (client) ID z punktu 1,
- **Nazwa** – dowolna, np. `RIS5`,
- **Identyfikator użytkownika** – użytkownik D365, w imieniu którego działa integracja. Musi mieć dostęp do *Zarządzania danymi* (np. rola *Data management administrator*) i do firm, których dotyczą zadania.

Dla typów Wysyłka i Pobieranie dodatkowo, w zadaniu cyklicznym D365 (*recurring data job*) wskaż tę aplikację w polu **Application ID** i zaznacz *Enabled* – patrz [Przykłady](05-configuring-jobs.md#upload).

## 3. Połączenie w RIS

*Połączenia → Nowe połączenie* (rola Admin).

![Formularz połączenia](img/connection-dialog.png)

| Pole | Przykład | Uwagi |
|---|---|---|
| Nazwa | `CONTOSO-PROD` | Zadania odwołują się do tej nazwy; nie można jej później zmienić. |
| Adres AOS | `https://contoso-prod.operations.dynamics.com` | Adres środowiska D365 (https). |
| Tenant | `contoso.onmicrosoft.com` | Domena lub identyfikator tenanta. |
| Client id | `aaaaaaaa-bbbb-…` | Application (client) ID. |
| Logowanie | **Client secret** albo **Certyfikat** | Widoczne są tylko pola wybranej metody; dane drugiej metody są usuwane przy zapisie. |
| Client secret | (wartość) | Tylko do zapisu: nigdy nie jest wyświetlany ani zwracany przez API. Puste pole przy edycji = zostaw zapisany. |
| Certyfikat | wybór z listy | Lista certyfikatów z kluczem prywatnym z magazynu serwera, z datą ważności; można też wkleić odcisk. |

Przycisk **Testuj połączenie** w oknie sprawdza wartości z formularza **przed zapisem** – kontem usługi loguje się do Entra ID i odczytuje listę projektów danych. Wynik pojawia się pod przyciskiem.

![Lista połączeń z ostatnim testem](img/connections.png)

Na liście połączeń kolumna **Ostatni test** pokazuje wynik i czas ostatniego testu, przycisk ⟲ w wierszu testuje jedno połączenie, a **Testuj wszystkie** – wszystkie naraz. Liczba w kolumnie *Używane przez zadania* prowadzi do listy zadań przefiltrowanej po połączeniu. Połączenia używanego przez zadania nie można usunąć – RIS pokaże, które zadania z niego korzystają.

## 4. Serwery SFTP (opcjonalnie)

*Serwery SFTP → Nowy serwer SFTP*. Logowanie **hasłem** albo **kluczem prywatnym** (ścieżka do pliku OpenSSH na serwerze RIS i opcjonalne hasło klucza). **Testuj połączenie** loguje się i listuje wskazany folder zdalny.

![Formularz serwera SFTP](img/sftp-dialog.png)

## 5. Ustawienia osobiste

W górnym pasku: 🌐 język (polski/angielski), ☀/🌙 motyw (jasny, ciemny, systemowy), 🕒 strefa wyświetlania czasu (serwera albo UTC), ❔ ta instrukcja. Wybór jest zapamiętywany w przeglądarce.
