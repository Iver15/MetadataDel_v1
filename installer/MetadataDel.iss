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
UsePreviousTasks=yes
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupIconFile=..\MetadataDel.Cli\app.ico
ChangesAssociations=yes
ChangesEnvironment=yes
DisableWelcomePage=no
DisableReadyMemo=no
WizardImageFile=images\wizard-202.bmp,images\wizard-253.bmp,images\wizard-303.bmp
WizardSmallImageFile=images\wizard-small-58.bmp,images\wizard-small-73.bmp,images\wizard-small-87.bmp,images\wizard-small-116.bmp

[Files]
; Ожидается, что предварительно выполнен publish в каталог ..\publish
Source: "{#MyPublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb,*.xml"

[InstallDelete]
; Прежние версии ставили exiftool в {app}\tools; очистка PDF больше его не использует.
Type: filesandordirs; Name: "{app}\tools"

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "ctxmenu"; Description: "Пункт «Удалить метаданные» в контекстном меню Проводника"; GroupDescription: "Быстрый доступ:"; Flags: checkedonce
Name: "sendto"; Description: "Пункт «Удалить метаданные» в меню «Отправить»"; GroupDescription: "Быстрый доступ:"; Flags: checkedonce
Name: "desktopicon"; Description: "Ярлык MetadataDel на рабочем столе"; GroupDescription: "Быстрый доступ:"; Flags: unchecked

[Run]
Filename: "{app}\{#MyAppExeName}"; Parameters: "--install-shell"; Flags: runhidden waituntilterminated skipifdoesntexist; Tasks: ctxmenu; StatusMsg: "Настройка контекстного меню..."
Filename: "{app}\{#MyAppExeName}"; Description: "Открыть MetadataDel"; Flags: nowait postinstall skipifsilent shellexec; WorkingDir: "{app}"

[UninstallRun]
Filename: "{app}\{#MyAppExeName}"; Parameters: "--uninstall-shell"; Flags: runhidden waituntilterminated skipifdoesntexist; RunOnceId: "MetadataDelUninstallShell"; StatusMsg: "Удаление контекстного меню..."

[UninstallDelete]
Type: files; Name: "{userappdata}\Microsoft\Windows\SendTo\{#MySendToFileName}"
; Журналы содержат пути очищенных файлов, поэтому удаляются вместе с программой.
Type: filesandordirs; Name: "{localappdata}\MetadataDel\logs"
Type: files; Name: "{localappdata}\MetadataDel\settings.json"
Type: dirifempty; Name: "{localappdata}\MetadataDel"

[Icons]
Name: "{group}\{#MyShortcutName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\app.ico"
Name: "{autodesktop}\{#MyShortcutName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\app.ico"; Tasks: desktopicon
Name: "{group}\Удалить MetadataDel"; Filename: "{uninstallexe}"

[Code]
procedure ApplyBranding();
begin
  WizardForm.Font.Name := 'Segoe UI';

  WizardForm.WelcomeLabel1.Caption := 'Установка MetadataDel';
  WizardForm.WelcomeLabel1.Font.Name := 'Segoe UI Semibold';
  WizardForm.WelcomeLabel1.Font.Size := 15;
  WizardForm.WelcomeLabel1.Font.Style := [];

  WizardForm.WelcomeLabel2.Caption :=
    'MetadataDel удаляет автора, историю правок, комментарии и другие скрытые сведения ' +
    'из PDF, Word и Excel перед отправкой документа.' + #13#10#13#10 +
    'Файлы обрабатываются только на этом компьютере. Установка займёт меньше минуты ' +
    'и не требует прав администратора.';
  WizardForm.WelcomeLabel2.Font.Name := 'Segoe UI';

  WizardForm.PageNameLabel.Font.Name := 'Segoe UI Semibold';
  WizardForm.PageNameLabel.Font.Style := [];
  WizardForm.PageNameLabel.Font.Size := 11;
  WizardForm.PageDescriptionLabel.Font.Name := 'Segoe UI';

  WizardForm.FinishedHeadingLabel.Font.Name := 'Segoe UI Semibold';
  WizardForm.FinishedHeadingLabel.Font.Size := 15;
  WizardForm.FinishedHeadingLabel.Font.Style := [];
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

function FinishedText(): string;
begin
  Result := 'Откройте MetadataDel из меню «Пуск» и перетащите документы в окно.';
  if WizardIsTaskSelected('ctxmenu') then
    Result := Result + #13#10#13#10 + 'Или щёлкните файл правой кнопкой мыши в Проводнике и выберите «Удалить метаданные». ' +
      'В Windows 11 пункт находится в разделе «Показать дополнительные параметры».';
  if WizardIsTaskSelected('sendto') then
    Result := Result + #13#10#13#10 + 'Также доступно меню «Отправить» → «Удалить метаданные».';
  Result := Result + #13#10#13#10 + 'Из Проводника файлы перезаписываются без резервных копий. ' +
    'Для важных документов используйте окно программы с включёнными копиями.';
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = wpSelectTasks then
  begin
    WizardForm.PageNameLabel.Caption := 'Быстрый доступ';
    WizardForm.PageDescriptionLabel.Caption := 'Откуда удобнее запускать очистку? Всё можно изменить позже в настройках программы.';
  end
  else if CurPageID = wpReady then
  begin
    WizardForm.PageNameLabel.Caption := 'Всё готово к установке';
    WizardForm.PageDescriptionLabel.Caption := 'Проверьте параметры и нажмите «Установить».';
  end
  else if CurPageID = wpFinished then
  begin
    WizardForm.FinishedHeadingLabel.Caption := 'MetadataDel установлен';
    WizardForm.FinishedLabel.Caption := FinishedText();
  end;
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo, MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
begin
  Result :=
    'Программа:' + NewLine +
    Space + 'MetadataDel {#MyAppVersion}' + NewLine + NewLine +
    'Папка установки:' + NewLine +
    Space + ExpandConstant('{app}');

  if MemoTasksInfo <> '' then
    Result := Result + NewLine + NewLine + MemoTasksInfo;
end;
