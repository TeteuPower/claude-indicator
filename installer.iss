; Instalador do Claude Indicator (Inno Setup 6)
; Gere com: .\build.ps1   (ou abra este arquivo no Inno Setup Compiler)

#define MyAppName "Claude Indicator"
#define MyAppVersion "1.8.3"
#define MyAppExe "ClaudeIndicator.exe"

[Setup]
AppId={{7C4E2B10-9A3F-4D6E-B8C1-2F5A9D7E4B31}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher=Claude Indicator
AppPublisherURL=https://github.com/TeteuPower/claude-indicator
AppSupportURL=https://github.com/TeteuPower/claude-indicator/issues
AppUpdatesURL=https://github.com/TeteuPower/claude-indicator/releases
VersionInfoVersion={#MyAppVersion}
UninstallDisplayName={#MyAppName}
DefaultDirName={autopf}\Claude Indicator
DefaultGroupName=Claude Indicator
DisableProgramGroupPage=yes
; numa atualização a pasta e as opções anteriores são reaproveitadas sem perguntar de novo
DisableDirPage=auto
UsePreviousAppDir=yes
UsePreviousTasks=yes
; o app rodando é fechado pelo código abaixo, não pelo Restart Manager (a janela fica oculta na bandeja)
CloseApplications=no
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=dist
OutputBaseFilename=ClaudeIndicator-Setup-{#MyAppVersion}
SetupIconFile=src\ClaudeIndicator\Assets\app.ico
UninstallDisplayIcon={app}\{#MyAppExe}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "Criar atalho na área de trabalho"; GroupDescription: "Atalhos:"; Flags: unchecked
Name: "startup"; Description: "Iniciar o Claude Indicator junto com o Windows"; GroupDescription: "Inicialização:"

[Files]
Source: "publish\{#MyAppExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"
Name: "{group}\Desinstalar {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
    ValueName: "ClaudeIndicator"; ValueData: """{app}\{#MyAppExe}"" --minimized"; \
    Flags: uninsdeletevalue; Tasks: startup

[Run]
Filename: "{app}\{#MyAppExe}"; Description: "Abrir o {#MyAppName} agora"; Flags: nowait postinstall skipifsilent
; atualização feita pelo próprio app: ele foi fechado para a troca do executável, então
; quem o inicia de volta é o instalador
Filename: "{app}\{#MyAppExe}"; Parameters: "--minimized"; Flags: nowait; Check: WizardSilent

[UninstallDelete]
Type: filesandordirs; Name: "{userappdata}\ClaudeIndicator"

[Code]
const
  UninstallKey =
    'Software\Microsoft\Windows\CurrentVersion\Uninstall\{7C4E2B10-9A3F-4D6E-B8C1-2F5A9D7E4B31}_is1';

var
  IsUpgrade: Boolean;

{ Fecha a instância que estiver na bandeja: sem isso o executável fica em uso e não pode ser trocado.
  As preferências ficam em %APPDATA% e são gravadas na hora em que mudam, então nada se perde. }
procedure StopRunningApp;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM {#MyAppExe} /F', '',
       SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(800);
end;

function InitializeSetup(): Boolean;
var
  Previous: String;
begin
  IsUpgrade := RegQueryStringValue(HKA, UninstallKey, 'UninstallString', Previous);
  Result := True;
end;

{ Atualização: nada de perguntar pasta, tarefas ou confirmação de novo. }
function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := IsUpgrade and ((PageID = wpSelectTasks) or (PageID = wpReady));
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopRunningApp;
  Result := '';
end;

function InitializeUninstall(): Boolean;
begin
  StopRunningApp;
  Result := True;
end;
