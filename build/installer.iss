; Inno Setup: установщик VieNLink (второй вариант поставки — рядом с портативным ZIP).
; Компилируется из build\publish-installer.ps1:
;   ISCC.exe /DAppVersion=0.9.0 build\installer.iss
; Ставится в профиль пользователя, чтобы рядом с exe можно было писать папку zapret.

#define AppName "VieNLink"
#define AppPublisher "VieN"
#define AppExe "VieNLink.exe"
#define AppChannel "бета"

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
AppId={{3F2B1C7E-5D4A-4E8B-9A61-7C2D0F5B8E14}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion} ({#AppChannel})
AppPublisher={#AppPublisher}
AppCopyright=© 2026 {#AppPublisher}
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UsePreviousAppDir=yes
PrivilegesRequired=lowest
OutputDir=..\release
OutputBaseFilename={#AppName}-{#AppVersion}-setup
SetupIconFile=..\VieNLink\Assets\VieNLink.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName} {#AppVersion} ({#AppChannel})
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
VersionInfoProductName={#AppName} ({#AppChannel})
VersionInfoCompany={#AppPublisher}
VersionInfoDescription=Установщик {#AppName} — бета-версии клиента свободных узлов

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Папка уже собрана publish-release.ps1: exe, core\sing-box.exe, zapret\, ЧИТАЙ.txt.
Source: "..\release\VieNLink-{#AppVersion}-win-x64\*"; DestDir: "{app}"; \
    Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"; AppUserModelID: "VieN.VieNLink"
Name: "{group}\Читать перед запуском"; Filename: "{app}\ЧИТАЙ.txt"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon; AppUserModelID: "VieN.VieNLink"

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; \
    Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Пользовательские данные (%LOCALAPPDATA%\VieNLink: узлы, настройки, логи) не удаляются —
; при повторной установке они подхватятся автоматически.
Type: filesandordirs; Name: "{app}\zapret"
Type: filesandordirs; Name: "{app}\core"

[Code]
// Перед удалением закрываем работающее приложение: иначе оно останется висеть
// в трее, а его папка (zapret, core) будет пересоздана при следующем включении обхода.
function InitializeUninstall(): Boolean;
var
  ResultCode: Integer;
begin
  Result := True;
  if not Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM VieNLink.exe', '',
      SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    exit;

  // 0 — процесс закрыт, 128 — процесса не было, остальное — нет прав (приложение
  // запущено с повышением UAC). Тогда предлагаем закрыть его от имени администратора.
  if (ResultCode <> 0) and (ResultCode <> 128) and (not UninstallSilent()) then
  begin
    if MsgBox('VieNLink запущен с правами администратора и не закрывается.' + #13#10 +
        'Закрыть его сейчас? Потребуется подтверждение UAC.',
        mbConfirmation, MB_YESNO) = IDYES then
      ShellExec('runas', ExpandConstant('{sys}\taskkill.exe'), '/F /IM VieNLink.exe', '',
        SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;
