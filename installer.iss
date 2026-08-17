; Instalador do Claude Indicator (Inno Setup 6)
; Gere com: .\build.ps1   (ou abra este arquivo no Inno Setup Compiler)

#define MyAppName "Claude Indicator"
#define MyAppVersion "1.0.0"
#define MyAppExe "ClaudeIndicator.exe"

[Setup]
AppId={{7C4E2B10-9A3F-4D6E-B8C1-2F5A9D7E4B31}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher=Claude Indicator
DefaultDirName={autopf}\Claude Indicator
DefaultGroupName=Claude Indicator
DisableProgramGroupPage=yes
DisableDirPage=no
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

[UninstallDelete]
Type: filesandordirs; Name: "{userappdata}\ClaudeIndicator"
