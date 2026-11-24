; Inno Setup: отдельный установщик «VieNLink Браузер».
; Собирается из build\publish-browser.ps1:
;   ISCC.exe /DAppVersion=1.0.0 build\browser-installer.iss
; Ставится в профиль пользователя, отдельно от VPN-приложения, и обновляется независимо.

#define AppName "VieNLink Browser"
#define AppDisplayName "VieNLink Браузер"
#define AppPublisher "VieN"
#define AppExe "VieNLink.Browser.exe"

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
AppId={{9C1D2E4A-7B36-4F58-8E21-5A4B3C9D0F27}
AppName={#AppDisplayName}
AppVersion={#AppVersion}
AppVerName={#AppDisplayName} {#AppVersion}
AppPublisher={#AppPublisher}
AppCopyright=© 2026 {#AppPublisher}
DefaultDirName={localappdata}\Programs\VieNLink Browser
DefaultGroupName={#AppDisplayName}
DisableProgramGroupPage=yes
UsePreviousAppDir=yes
PrivilegesRequired=lowest
OutputDir=..\release
OutputBaseFilename=VieNLink-Browser-{#AppVersion}-setup
SetupIconFile=..\VieNLink.Browser\Assets\browser-icon.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppDisplayName} {#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
SetupLogging=yes
MinVersion=10.0.10240
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppDisplayName}
VersionInfoCompany={#AppPublisher}
VersionInfoDescription=Установщик {#AppDisplayName} — браузер VieNLink на WebView2

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\release\VieNLink-Browser-{#AppVersion}-win-x64\*"; DestDir: "{app}"; \
    Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppDisplayName}"; Filename: "{app}\{#AppExe}"; AppUserModelID: "VieN.VieNLink.Browser"
Name: "{autodesktop}\{#AppDisplayName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon; \
    AppUserModelID: "VieN.VieNLink.Browser"

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppDisplayName}}"; \
    Flags: nowait postinstall skipifsilent

[Code]
// Перед удалением закрываем браузер, иначе exe останется занятым.
function InitializeUninstall(): Boolean;
var
  ResultCode: Integer;
begin
  Result := True;
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM VieNLink.Browser.exe', '',
      SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;
