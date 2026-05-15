#define MyAppName "MetadataDel"
#define MyAppPublisher "MetadataDel"
#define MyAppExeName "MetadataDel.exe"
#define MyPublishDir "..\publish"
#define MyAppVersion GetVersionNumbersString("..\publish\MetadataDel.exe")
#define MySendToFileName "Удалить метаданные.cmd"
#define MyShortcutName "MetadataDel"

[Setup]
AppId={{D08D9E5F-1E8E-4E3F-AF9C-BF3E3D7C2B0D}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}
VersionInfoDescription=Установка MetadataDel
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableDirPage=yes
DisableProgramGroupPage=yes
OutputBaseFilename={#MyAppName}-Setup-{#MyAppVersion}
OutputDir=.
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
WizardStyle=modern
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupIconFile=..\MetadataDel.Cli\app.ico
ChangesAssociations=yes
ChangesEnvironment=yes
DisableWelcomePage=no
DisableReadyMemo=no

[Files]
; Ожидается, что предварительно выполнен publish в каталог ..\publish
Source: "{#MyPublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb,*.xml"

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "ctxmenu"; Description: "Добавить пункт контекстного меню Проводника"; Flags: checkedonce
Name: "sendto"; Description: "Добавить пункт в меню Отправить"; Flags: checkedonce
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе"; Flags: unchecked

[Run]
Filename: "{app}\{#MyAppExeName}"; Parameters: "--install-shell"; Flags: runhidden waituntilterminated skipifdoesntexist; Tasks: ctxmenu; StatusMsg: "Настройка контекстного меню..."
Filename: "{app}\{#MyAppExeName}"; Description: "Открыть MetadataDel"; Flags: nowait postinstall skipifsilent unchecked shellexec; WorkingDir: "{app}"

[UninstallRun]
Filename: "{app}\{#MyAppExeName}"; Parameters: "--uninstall-shell"; Flags: runhidden waituntilterminated skipifdoesntexist; RunOnceId: "MetadataDelUninstallShell"; StatusMsg: "Удаление контекстного меню..."

[UninstallDelete]
Type: files; Name: "{userappdata}\Microsoft\Windows\SendTo\{#MySendToFileName}"

[Icons]
Name: "{group}\{#MyShortcutName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\app.ico"
Name: "{autodesktop}\{#MyShortcutName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\app.ico"; Tasks: desktopicon
Name: "{group}\Удалить MetadataDel"; Filename: "{uninstallexe}"

[Code]
procedure ApplyBranding();
begin
  WizardForm.Font.Name := 'Segoe UI';

  WizardForm.WelcomeLabel1.Caption := 'MetadataDel';
  WizardForm.WelcomeLabel1.Font.Name := 'Segoe UI Semibold';
  WizardForm.WelcomeLabel1.Font.Size := 16;
  WizardForm.WelcomeLabel1.Font.Style := [fsBold];

  WizardForm.WelcomeLabel2.Caption :=
    'Быстрая и аккуратная установка без ручной настройки.' + #13#10#13#10 +
    'Setup установит программу, exiftool и подготовит интеграцию с Проводником.';
  WizardForm.WelcomeLabel2.Font.Name := 'Segoe UI';

  WizardForm.PageNameLabel.Font.Name := 'Segoe UI Semibold';
  WizardForm.PageNameLabel.Font.Style := [fsBold];
  WizardForm.PageNameLabel.Font.Size := 12;

  WizardForm.PageDescriptionLabel.Font.Name := 'Segoe UI';
  WizardForm.PageDescriptionLabel.Caption := 'Один установщик для программы, exiftool и интеграции с Проводником.';

  WizardForm.SelectTasksLabel.Caption := 'Выберите дополнительные действия:';
  WizardForm.ReadyLabel.Caption := 'Проверьте параметры установки. Когда всё готово, нажмите "Установить".';
end;

procedure InitializeWizard();
begin
  ApplyBranding();
end;

function GetSendToScriptPath(): string;
begin
  Result := ExpandConstant('{userappdata}\Microsoft\Windows\SendTo\{#MySendToFileName}');
end;

function BuildSendToScript(): string;
begin
  Result :=
    '@echo off' + #13#10 +
    '"' + ExpandConstant('{app}\{#MyAppExeName}') + '" --log %*' + #13#10;
end;

procedure InstallOrRemoveSendTo();
var
  ScriptPath: string;
begin
  ScriptPath := GetSendToScriptPath();

  if WizardIsTaskSelected('sendto') then
  begin
    if not SaveStringToFile(ScriptPath, BuildSendToScript(), False) then
      MsgBox('Не удалось создать пункт "Отправить -> Удалить метаданные".', mbError, MB_OK);
  end
  else if FileExists(ScriptPath) then
  begin
    DeleteFile(ScriptPath);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    InstallOrRemoveSendTo();
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = wpSelectTasks then
  begin
    WizardForm.PageNameLabel.Caption := 'Дополнительные возможности';
    WizardForm.PageDescriptionLabel.Caption := 'Выберите, какие точки входа MetadataDel нужно создать для пользователя.';
  end
  else if CurPageID = wpReady then
  begin
    WizardForm.PageNameLabel.Caption := 'Всё готово к установке';
    WizardForm.PageDescriptionLabel.Caption := 'MetadataDel будет установлен вместе с exiftool и нужными служебными файлами.';
    WizardForm.ReadyLabel.Caption := 'Нажмите "Установить", чтобы начать установку.';
  end
  else if CurPageID = wpFinished then
  begin
    WizardForm.FinishedHeadingLabel.Caption := 'MetadataDel установлен';
    WizardForm.FinishedHeadingLabel.Font.Name := 'Segoe UI Semibold';
    WizardForm.FinishedHeadingLabel.Font.Style := [fsBold];
    WizardForm.FinishedLabel.Caption :=
      'Программа готова к работе. Контекстное меню и пункт "Отправить" можно использовать сразу после завершения установки.';
  end
  else
  begin
    ApplyBranding();
  end;
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo, MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
begin
  Result :=
    'Будет установлено:' + NewLine +
    Space + '- MetadataDel' + NewLine +
    Space + '- exiftool и служебные файлы для глубокой очистки PDF' + NewLine +
    Space + '- интеграция с Проводником и пункт "Отправить" при выборе соответствующих опций' + NewLine + NewLine +
    'Папка установки:' + NewLine +
    Space + ExpandConstant('{app}');

  if MemoTasksInfo <> '' then
    Result := Result + NewLine + NewLine + 'Дополнительно:' + NewLine + MemoTasksInfo;
end;
