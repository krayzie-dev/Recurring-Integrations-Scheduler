; Recurring Integrations Scheduler 5 - installer (Inno Setup 6).
; Build with scripts\build-installer.ps1 (publishes the server and the CLI, passes the defines below).
;
; Silent install, e.g.:
;   RIS5-Setup-5.0.0.exe /VERYSILENT /SUPPRESSMSGBOXES /ACCOUNT="CONTOSO\svc-ris$" /HTTPPORT=5080 /HTTPSPORT=5443
;     /CERT=<thumbprint> /ADMINS="CONTOSO\RIS-Admins;CONTOSO\jdoe" /OPERATORS="CONTOSO\RIS-Operators" /IMPORT4X=1
;   /ACCOUNT: LocalSystem | LocalService | NetworkService | DOMAIN\user (+ /PASSWORD=...) | DOMAIN\gmsa$
;   /RECONFIGURE=1 on upgrade: apply the given settings instead of keeping the current ones.

#ifndef AppVersion
  #define AppVersion "5.0.0-local"
#endif
#ifndef AppVersionNumeric
  #define AppVersionNumeric "5.0.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\..\artifacts\publish"
#endif
#ifndef OutputDir
  #define OutputDir "..\..\artifacts"
#endif

#define ServiceName "RIS5"
#define FirewallRule "RIS5 HTTPS"

[Setup]
AppId={{6E0F4A52-5B3C-4E0B-9C61-2F7B6D2E5A15}
AppName=Recurring Integrations Scheduler 5
AppVersion={#AppVersion}
AppVerName=Recurring Integrations Scheduler {#AppVersion}
AppPublisher=Recurring Integrations Scheduler
AppPublisherURL=https://github.com/krayzie-dev/Recurring-Integrations-Scheduler
VersionInfoVersion={#AppVersionNumeric}
DefaultDirName={autopf}\RIS5
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#OutputDir}
OutputBaseFilename=RIS5-Setup-{#AppVersion}
SetupIconFile=..\Recurring Integrations Scheduler.ico
UninstallDisplayIcon={app}\RIS.Server.exe
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
CloseApplications=no
ChangesEnvironment=yes
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "polish"; MessagesFile: "compiler:Languages\Polish.isl"

[Tasks]
Name: "cliPath"; Description: "{cm:TaskCliPath}"

[Files]
Source: "{#PublishDir}\server\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PublishDir}\cli\*"; DestDir: "{app}\cli"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "setup-helpers.ps1"; DestDir: "{app}\setup"; Flags: ignoreversion

[Dirs]
Name: "{commonappdata}\RIS5"; Flags: uninsneveruninstall

[Registry]
Root: HKLM; Subkey: "SYSTEM\CurrentControlSet\Control\Session Manager\Environment"; ValueType: expandsz; ValueName: "Path"; \
    ValueData: "{olddata};{app}\cli"; Tasks: cliPath; Check: NeedsAddPath(ExpandConstant('{app}\cli'))

[CustomMessages]
english.TaskCliPath=Add the "ris" command line to PATH
polish.TaskCliPath=Dodaj wiersz poleceń "ris" do zmiennej PATH
english.AccountCaption=Service account
polish.AccountCaption=Konto usługi
english.AccountDescription=Account the RIS 5 service runs as.
polish.AccountDescription=Konto, na którym działa usługa RIS 5.
english.AccountHint=Network shares (UNC paths) need a domain account or a group managed service account (gMSA).
polish.AccountHint=Udziały sieciowe (ścieżki UNC) wymagają konta domenowego lub gMSA.
english.AccountDomain=Domain account or gMSA (DOMAIN\name, gMSA ends with $)
polish.AccountDomain=Konto domenowe lub gMSA (DOMENA\nazwa, gMSA kończy się na $)
english.CredentialsCaption=Domain account
polish.CredentialsCaption=Konto domenowe
english.CredentialsDescription=Name and password of the service account (empty password for a gMSA).
polish.CredentialsDescription=Nazwa i hasło konta usługi (dla gMSA hasło puste).
english.UserName=Account (DOMAIN\name):
polish.UserName=Konto (DOMENA\nazwa):
english.Password=Password:
polish.Password=Hasło:
english.InvalidCredentials=The name or password is not correct: %1
polish.InvalidCredentials=Nieprawidłowa nazwa lub hasło: %1
english.NetworkCaption=Access
polish.NetworkCaption=Dostęp
english.NetworkDescription=Web UI and REST API. Local HTTP is always on; remote access uses HTTPS.
polish.NetworkDescription=Interfejs WWW i REST API. Lokalny HTTP jest zawsze włączony; dostęp zdalny przez HTTPS.
english.HttpPort=Local HTTP port (localhost only):
polish.HttpPort=Lokalny port HTTP (tylko localhost):
english.HttpsPort=HTTPS port for remote access (empty = no remote access):
polish.HttpsPort=Port HTTPS dla dostępu zdalnego (puste = bez dostępu zdalnego):
english.Certificate=Certificate thumbprint (LocalMachine\My, with private key):
polish.Certificate=Odcisk certyfikatu (LocalMachine\My, z kluczem prywatnym):
english.Admins=Administrators (groups/users separated by ;):
polish.Admins=Administratorzy (grupy/użytkownicy oddzieleni ;):
english.Operators=Operators (run, pause, resume; optional):
polish.Operators=Operatorzy (uruchamianie, wstrzymywanie; opcjonalnie):
english.InvalidPort=Port must be a number between 1 and 65535.
polish.InvalidPort=Port musi być liczbą od 1 do 65535.
english.CertificateRequired=Enter the certificate thumbprint for HTTPS.
polish.CertificateRequired=Podaj odcisk certyfikatu dla HTTPS.
english.CertificateInvalid=Certificate check failed: %1
polish.CertificateInvalid=Certyfikat nie przeszedł weryfikacji: %1
english.ImportCaption=Import from RIS 4.x
polish.ImportCaption=Import z RIS 4.x
english.ImportDescription=A RIS 4.x schedule was found on this server.
polish.ImportDescription=Na tym serwerze znaleziono harmonogram RIS 4.x.
english.ImportOption=Import the 4.x jobs as PAUSED jobs (resume them in the UI after checking)
polish.ImportOption=Zaimportuj zadania 4.x jako WSTRZYMANE (wznów je w UI po sprawdzeniu)
english.ImportHint=Secrets are not imported: set them in the UI (Connections) or with "ris db set-secret". Report: %1
polish.ImportHint=Sekrety nie są importowane: ustaw je w UI (Połączenia) lub poleceniem "ris db set-secret". Raport: %1
english.KeepSettings=Existing settings in %1 are kept (run the setup with /RECONFIGURE=1 to change them).
polish.KeepSettings=Zachowano istniejące ustawienia w %1 (uruchom instalator z /RECONFIGURE=1, aby je zmienić).
english.ServiceRunning=The service is running: %1%nOpen %2
polish.ServiceRunning=Usługa działa: %1%nOtwórz %2
english.ServiceNotResponding=The service did not answer. Check the Application event log and %1
polish.ServiceNotResponding=Usługa nie odpowiada. Sprawdź dziennik Aplikacja i %1
english.RemoveData=Remove the RIS 5 data (databases, keys, logs, backups) from %1?%n%nChoose No to keep them for a reinstall.
polish.RemoveData=Usunąć dane RIS 5 (bazy, klucze, logi, kopie) z %1?%n%nWybierz Nie, aby je zachować do ponownej instalacji.

[Code]
const
  LOGON32_LOGON_NETWORK = 3;
  LOGON32_PROVIDER_DEFAULT = 0;

var
  AccountPage: TInputOptionWizardPage;
  CredentialsPage: TInputQueryWizardPage;
  NetworkPage: TInputQueryWizardPage;
  ImportPage: TInputOptionWizardPage;
  Upgrade, Reconfigure: Boolean;
  Schedule4x, ResultText: String;

function LogonUser(lpszUsername, lpszDomain, lpszPassword: String; dwLogonType, dwLogonProvider: Integer; var phToken: THandle): Boolean;
  external 'LogonUserW@advapi32.dll stdcall';
function CloseHandle(hObject: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';

function DataDir(): String;
begin
  Result := ExpandConstant('{commonappdata}\RIS5');
end;

function MachineSettings(): String;
begin
  Result := DataDir() + '\appsettings.json';
end;

function NeedsAddPath(Dir: String): Boolean;
var
  Path: String;
begin
  if not RegQueryStringValue(HKLM, 'SYSTEM\CurrentControlSet\Control\Session Manager\Environment', 'Path', Path) then
    Path := '';
  Result := Pos(';' + Uppercase(Dir) + ';', ';' + Uppercase(Path) + ';') = 0;
end;

{ DOMAIN\user of the person running the setup (the elevated user keeps the original name). }
function InstallingUser(): String;
var
  Domain: String;
begin
  Domain := GetEnv('USERDOMAIN');
  if Domain = '' then Domain := GetComputerNameString();
  Result := Domain + '\' + GetUserNameString();
end;

function Param(Name, Default: String): String;
begin
  Result := ExpandConstant('{param:' + Name + '|' + Default + '}');
end;

{ Runs a command hidden, captures its output into Output. }
function RunCaptured(Command, Arguments: String; var Output: String): Integer;
var
  LogFile: String;
  Text: AnsiString;
begin
  LogFile := ExpandConstant('{tmp}\ris5-command.txt');
  DeleteFile(LogFile);
  if not Exec(ExpandConstant('{cmd}'), '/C ""' + Command + '" ' + Arguments + ' > "' + LogFile + '" 2>&1"', '', SW_HIDE, ewWaitUntilTerminated, Result) then
    Result := -1;
  if LoadStringFromFile(LogFile, Text) then Output := Trim(String(Text)) else Output := '';
  Log(Command + ' ' + Arguments + ' -> ' + IntToStr(Result) + ': ' + Output);
end;

function Helper(Action, Arguments: String; var Output: String): Integer;
var
  Script: String;
begin
  { Always the helper of THIS setup: on upgrade the installed one may be older (or broken). }
  Script := ExpandConstant('{tmp}\setup-helpers.ps1');
  if not FileExists(Script) then ExtractTemporaryFile('setup-helpers.ps1');
  Result := RunCaptured(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -ExecutionPolicy Bypass -File "' + Script + '" -Action ' + Action + ' ' + Arguments, Output);
end;

function ServiceExists(): Boolean;
var
  Code: Integer;
begin
  Result := Exec(ExpandConstant('{sys}\sc.exe'), 'query {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0);
end;

function SelectedAccount(): String;
begin
  case AccountPage.SelectedValueIndex of
    0: Result := 'LocalSystem';
    1: Result := 'NT AUTHORITY\LocalService';
    2: Result := 'NT AUTHORITY\NetworkService';
  else
    Result := Trim(CredentialsPage.Values[0]);
  end;
end;

function IsBuiltInAccount(): Boolean;
begin
  Result := AccountPage.SelectedValueIndex < 3;
end;

function IsPort(Value: String): Boolean;
var
  Port: Integer;
begin
  Port := StrToIntDef(Trim(Value), 0);
  Result := (Port > 0) and (Port < 65536);
end;

function JsonString(Value: String): String;
begin
  Result := Value;
  StringChangeEx(Result, '\', '\\', True);
  StringChangeEx(Result, '"', '\"', True);
  Result := '"' + Result + '"';
end;

{ "a; b" -> ["a", "b"] }
function JsonList(Value: String): String;
var
  Item: String;
  Position: Integer;
begin
  Result := '';
  Value := Value + ';';
  while Value <> '' do
  begin
    Position := Pos(';', Value);
    Item := Trim(Copy(Value, 1, Position - 1));
    Delete(Value, 1, Position);
    if Item <> '' then
    begin
      if Result <> '' then Result := Result + ', ';
      Result := Result + JsonString(Item);
    end;
  end;
  Result := '[' + Result + ']';
end;

procedure InitializeWizard();
var
  AccountParam: String;
begin
  Upgrade := ServiceExists() and FileExists(MachineSettings());
  Reconfigure := Param('RECONFIGURE', '0') = '1';

  AccountPage := CreateInputOptionPage(wpSelectTasks, CustomMessage('AccountCaption'), CustomMessage('AccountDescription'),
    CustomMessage('AccountHint'), True, False);
  AccountPage.Add('LocalSystem');
  AccountPage.Add('LocalService');
  AccountPage.Add('NetworkService');
  AccountPage.Add(CustomMessage('AccountDomain'));

  CredentialsPage := CreateInputQueryPage(AccountPage.ID, CustomMessage('CredentialsCaption'), CustomMessage('CredentialsDescription'), '');
  CredentialsPage.Add(CustomMessage('UserName'), False);
  CredentialsPage.Add(CustomMessage('Password'), True);

  AccountParam := Param('ACCOUNT', 'LocalService');
  if CompareText(AccountParam, 'LocalSystem') = 0 then AccountPage.SelectedValueIndex := 0
  else if CompareText(AccountParam, 'LocalService') = 0 then AccountPage.SelectedValueIndex := 1
  else if CompareText(AccountParam, 'NetworkService') = 0 then AccountPage.SelectedValueIndex := 2
  else
  begin
    AccountPage.SelectedValueIndex := 3;
    CredentialsPage.Values[0] := AccountParam;
    CredentialsPage.Values[1] := Param('PASSWORD', '');
  end;

  NetworkPage := CreateInputQueryPage(CredentialsPage.ID, CustomMessage('NetworkCaption'), CustomMessage('NetworkDescription'), '');
  NetworkPage.Add(CustomMessage('HttpPort'), False);
  NetworkPage.Add(CustomMessage('HttpsPort'), False);
  NetworkPage.Add(CustomMessage('Certificate'), False);
  NetworkPage.Add(CustomMessage('Admins'), False);
  NetworkPage.Add(CustomMessage('Operators'), False);
  NetworkPage.Values[0] := Param('HTTPPORT', '5080');
  NetworkPage.Values[1] := Param('HTTPSPORT', '');
  NetworkPage.Values[2] := Param('CERT', '');
  { S-1-5-32-544 = BUILTIN\Administrators on every Windows language. On the server itself UAC hands the browser a token in
    which Administrators is "deny only", so the installing user is added by name: otherwise nobody can log in locally. }
  NetworkPage.Values[3] := Param('ADMINS', 'S-1-5-32-544;' + InstallingUser());
  NetworkPage.Values[4] := Param('OPERATORS', '');

  Schedule4x := ExpandConstant('{commonpf32}\Recurring Integrations Scheduler\Schedule.xml');
  if not FileExists(Schedule4x) then Schedule4x := ExpandConstant('{commonpf64}\Recurring Integrations Scheduler\Schedule.xml');
  ImportPage := CreateInputOptionPage(NetworkPage.ID, CustomMessage('ImportCaption'), CustomMessage('ImportDescription'), Schedule4x, False, False);
  ImportPage.Add(CustomMessage('ImportOption'));
  ImportPage.Values[0] := Param('IMPORT4X', '1') = '1';
end;

function Configure(): Boolean;
begin
  Result := (not Upgrade) or Reconfigure;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
  if (PageID = AccountPage.ID) or (PageID = NetworkPage.ID) then Result := not Configure()
  else if PageID = CredentialsPage.ID then Result := (not Configure()) or IsBuiltInAccount()
  else if PageID = ImportPage.ID then Result := Upgrade or not FileExists(Schedule4x);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Account, Domain, User, Output: String;
  Token: THandle;
  Position: Integer;
begin
  Result := True;
  if CurPageID = CredentialsPage.ID then
  begin
    Account := Trim(CredentialsPage.Values[0]);
    { gMSA: no password to check }
    if (Account <> '') and (Copy(Account, Length(Account), 1) <> '$') then
    begin
      Position := Pos('\', Account);
      Domain := Copy(Account, 1, Position - 1);
      User := Copy(Account, Position + 1, Length(Account));
      if not LogonUser(User, Domain, CredentialsPage.Values[1], LOGON32_LOGON_NETWORK, LOGON32_PROVIDER_DEFAULT, Token) then
      begin
        MsgBox(FmtMessage(CustomMessage('InvalidCredentials'), [Account]), mbError, MB_OK);
        Result := False;
      end
      else
        CloseHandle(Token);
    end;
  end
  else if CurPageID = NetworkPage.ID then
  begin
    if not IsPort(NetworkPage.Values[0]) or ((Trim(NetworkPage.Values[1]) <> '') and not IsPort(NetworkPage.Values[1])) then
    begin
      MsgBox(CustomMessage('InvalidPort'), mbError, MB_OK);
      Result := False;
    end
    else if Trim(NetworkPage.Values[1]) <> '' then
    begin
      if Trim(NetworkPage.Values[2]) = '' then
      begin
        MsgBox(CustomMessage('CertificateRequired'), mbError, MB_OK);
        Result := False;
      end
      else if Helper('TestCertificate', '-Thumbprint "' + Trim(NetworkPage.Values[2]) + '"', Output) <> 0 then
      begin
        MsgBox(FmtMessage(CustomMessage('CertificateInvalid'), [Output]), mbError, MB_OK);
        Result := False;
      end;
    end;
  end;
end;

{ Stops the running service and backs up the databases before files are replaced. }
{ True when "sc query" reports the service as STOPPED. }
function ServiceStopped(): Boolean;
var
  Output: String;
begin
  RunCaptured(ExpandConstant('{sys}\sc.exe'), 'query {#ServiceName}', Output);
  Result := Pos('STOPPED', Uppercase(Output)) > 0;
end;

{ Stops the service without depending on PowerShell; running jobs are interrupted within seconds. }
function StopService(): Boolean;
var
  Code, Waited: Integer;
begin
  Result := ServiceStopped();
  if Result then Exit;
  Exec(ExpandConstant('{sys}\net.exe'), 'stop {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Waited := 0;
  while (not ServiceStopped()) and (Waited < 90) do
  begin
    Sleep(1000);
    Waited := Waited + 1;
  end;
  Result := ServiceStopped();
  Log('Service stopped: ' + IntToStr(Ord(Result)) + ' after ' + IntToStr(Waited) + ' s');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Backup: String;
begin
  Result := '';
  if ServiceExists() then
  begin
    if not StopService() then
    begin
      Result := 'The RIS5 service could not be stopped. Stop it (services.msc) and run the setup again.';
      Exit;
    end;
    Backup := DataDir() + '\backup\before-upgrade-' + GetDateTimeString('yyyymmdd-hhnnss', '-', '-');
    if FileExists(DataDir() + '\ris.db') then
    begin
      ForceDirectories(Backup);
      FileCopy(DataDir() + '\ris.db', Backup + '\ris.db', False);
      FileCopy(DataDir() + '\quartz.db', Backup + '\quartz.db', False);
      Log('Databases backed up to ' + Backup);
    end;
  end;
end;

procedure WriteMachineSettings();
var
  Json: String;
  Lines: TArrayOfString;
begin
  Json := '{' + #13#10 +
    '  "Urls": "http://localhost:' + Trim(NetworkPage.Values[0]) + '",' + #13#10 +
    '  "Ris": {' + #13#10;
  if Trim(NetworkPage.Values[1]) <> '' then
    Json := Json + '    "Https": { "Port": ' + Trim(NetworkPage.Values[1]) + ', "CertificateThumbprint": ' + JsonString(Trim(NetworkPage.Values[2])) + ' },' + #13#10;
  Json := Json +
    '    "Security": { "Roles": { "Admin": ' + JsonList(NetworkPage.Values[3]) + ', "Operator": ' + JsonList(NetworkPage.Values[4]) + ' } }' + #13#10 +
    '  }' + #13#10 + '}' + #13#10;
  ForceDirectories(DataDir());
  if FileExists(MachineSettings()) then
    FileCopy(MachineSettings(), MachineSettings() + '.' + GetDateTimeString('yyyymmdd-hhnnss', '-', '-') + '.bak', False);
  { UTF-8: group names may contain non-ASCII characters (e.g. Polish) }
  Lines := [Json];
  SaveStringsToUTF8File(MachineSettings(), Lines, False);
end;

{ HTTP port from the existing machine settings ("Urls": "http://localhost:NNNN"), for upgrades that keep the settings. }
function ExistingHttpPort(): String;
var
  Text: AnsiString;
  Content: String;
  Position, Index: Integer;
begin
  Result := '5080';
  if not LoadStringFromFile(MachineSettings(), Text) then Exit;
  Content := String(Text);
  Position := Pos('http://localhost:', Content);
  if Position = 0 then Exit;
  Index := Position + Length('http://localhost:');
  Result := '';
  while (Index <= Length(Content)) and (Content[Index] >= '0') and (Content[Index] <= '9') do
  begin
    Result := Result + Content[Index];
    Index := Index + 1;
  end;
  if Result = '' then Result := '5080';
end;

procedure Import4x();
var
  Ris, Folder, Output: String;
begin
  Ris := ExpandConstant('{app}\cli\ris.exe');
  Folder := DataDir() + '\import-4x';
  ForceDirectories(Folder);
  RunCaptured(Ris, 'convert-schedule "' + Schedule4x + '" -o "' + Folder + '\jobs.json"', Output);
  SaveStringToFile(Folder + '\import.log', Output + #13#10, False);
  RunCaptured(Ris, 'db import "' + Folder + '\jobs.json" --paused', Output);
  SaveStringToFile(Folder + '\import.log', Output + #13#10, True);
  ResultText := ResultText + #13#10 + FmtMessage(CustomMessage('ImportHint'), [Folder + '\import.log']);
end;

procedure InstallService();
var
  Exe, Account, Arguments, Output, HttpsPort, HttpPort: String;
  Code: Integer;
begin
  Exe := ExpandConstant('{app}\RIS.Server.exe');
  Helper('EventSource', '', Output);

  if not ServiceExists() then
  begin
    Exec(ExpandConstant('{sys}\sc.exe'), 'create {#ServiceName} binPath= "\"' + Exe + '\"" start= delayed-auto DisplayName= "Recurring Integrations Scheduler 5"',
      '', SW_HIDE, ewWaitUntilTerminated, Code);
    Exec(ExpandConstant('{sys}\sc.exe'), 'description {#ServiceName} "Runs D365 F&O data management jobs (RIS 5)."', '', SW_HIDE, ewWaitUntilTerminated, Code);
    Exec(ExpandConstant('{sys}\sc.exe'), 'failure {#ServiceName} reset= 86400 actions= restart/60000/restart/300000/restart/900000', '', SW_HIDE, ewWaitUntilTerminated, Code);
  end;

  if Configure() then
  begin
    Account := SelectedAccount();
    Arguments := 'config {#ServiceName} obj= "' + Account + '"';
    if (not IsBuiltInAccount()) and (CredentialsPage.Values[1] <> '') then
      Arguments := Arguments + ' password= "' + CredentialsPage.Values[1] + '"';
    if (AccountPage.SelectedValueIndex = 3) then Helper('GrantLogonRight', '-Account "' + Account + '"', Output);
    Exec(ExpandConstant('{sys}\sc.exe'), Arguments, '', SW_HIDE, ewWaitUntilTerminated, Code);
    if Code <> 0 then Log('sc config failed: ' + IntToStr(Code));

    if AccountPage.SelectedValueIndex <> 0 then
    begin
      { Data directory (databases, keys, logs): modify for the service account only. }
      RunCaptured(ExpandConstant('{sys}\icacls.exe'), '"' + DataDir() + '" /grant "' + Account + ':(OI)(CI)M" /T /Q', Output);
      HttpsPort := Trim(NetworkPage.Values[1]);
      if HttpsPort <> '' then Helper('GrantCertificateKey', '-Thumbprint "' + Trim(NetworkPage.Values[2]) + '" -Account "' + Account + '"', Output);
    end;

    Exec(ExpandConstant('{sys}\netsh.exe'), 'advfirewall firewall delete rule name="{#FirewallRule}"', '', SW_HIDE, ewWaitUntilTerminated, Code);
    HttpsPort := Trim(NetworkPage.Values[1]);
    if HttpsPort <> '' then
      Exec(ExpandConstant('{sys}\netsh.exe'), 'advfirewall firewall add rule name="{#FirewallRule}" dir=in action=allow protocol=TCP localport=' + HttpsPort,
        '', SW_HIDE, ewWaitUntilTerminated, Code);
  end;

  Exec(ExpandConstant('{sys}\sc.exe'), 'start {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, Code);
  if Configure() then HttpPort := Trim(NetworkPage.Values[0]) else HttpPort := ExistingHttpPort();
  if Helper('WaitHealth', '-Port ' + HttpPort, Output) = 0 then
    ResultText := FmtMessage(CustomMessage('ServiceRunning'), [Output, 'http://localhost:' + HttpPort + '/']) + ResultText
  else
    ResultText := FmtMessage(CustomMessage('ServiceNotResponding'), [DataDir() + '\logs']) + ResultText;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    if Configure() then WriteMachineSettings()
    else ResultText := #13#10 + FmtMessage(CustomMessage('KeepSettings'), [MachineSettings()]);
    if (not Upgrade) and FileExists(Schedule4x) and ImportPage.Values[0] then Import4x();
    InstallService();
  end;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if (CurPageID = wpFinished) and (ResultText <> '') then
    WizardForm.FinishedLabel.Caption := WizardForm.FinishedLabel.Caption + #13#10#13#10 + ResultText;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Code: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    StopService();
    Exec(ExpandConstant('{sys}\sc.exe'), 'delete {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, Code);
    Exec(ExpandConstant('{sys}\netsh.exe'), 'advfirewall firewall delete rule name="{#FirewallRule}"', '', SW_HIDE, ewWaitUntilTerminated, Code);
  end
  else if CurUninstallStep = usPostUninstall then
  begin
    if (not UninstallSilent()) and DirExists(DataDir()) and
       (MsgBox(FmtMessage(CustomMessage('RemoveData'), [DataDir()]), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES) then
      DelTree(DataDir(), True, True, True);
  end;
end;
