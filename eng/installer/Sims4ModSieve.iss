#ifndef AppVersion
  #error AppVersion must come from the project.
#endif
#ifndef SourceDirectory
  #error SourceDirectory is required.
#endif
#ifndef OutputDirectory
  #error OutputDirectory is required.
#endif

[Setup]
#ifdef TestDataDirectory
AppId={{C897868A-0997-463F-B377-A68A616F831E}-InstallerTests
AppName=Sims 4 Mod Sieve（安装器测试）
#else
AppId={{C897868A-0997-463F-B377-A68A616F831E}
AppName=Sims 4 Mod Sieve
#endif
AppVersion={#AppVersion}
AppPublisher=stella-wood
AppPublisherURL=https://github.com/stella-wood/Sims4ModSieve
AppSupportURL=https://github.com/stella-wood/Sims4ModSieve/issues
AppUpdatesURL=https://github.com/stella-wood/Sims4ModSieve/releases
VersionInfoVersion={#FileVersion}
VersionInfoProductTextVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\Sims4ModSieve
DisableProgramGroupPage=yes
DisableDirPage=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
SetupArchitecture=x64
MinVersion=10.0
SetupIconFile=..\..\src\Sims4ModSieve.Desktop\Assets\Sims4ModSieve.Setup.ico
UninstallDisplayIcon={app}\Sims4ModSieve.exe
OutputDir={#OutputDirectory}
OutputBaseFilename=Sims4ModSieve-{#AppVersion}-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=no
RestartApplications=no
#ifdef TestDataDirectory
SetupMutex=Sims4ModSieve.Setup.InstallerTests
#else
SetupMutex=Sims4ModSieve.Setup
#endif
Uninstallable=yes
LicenseFile={#SourceDirectory}\LICENSE

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Messages]
ConfirmUninstall=确定要卸载 Sims 4 Mod Sieve 吗？你的 Mods、存档和游戏文件都不会被删除。

[Tasks]
Name: "startmenu"; Description: "创建开始菜单快捷方式"
Name: "desktopicon"; Description: "创建桌面快捷方式"; Flags: unchecked

[Files]
Source: "{#SourceDirectory}\Sims4ModSieve.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDirectory}\*.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDirectory}\*"; DestDir: "{app}"; Excludes: "*.exe,*.dll"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
#ifdef TestShellDirectory
Name: "{#TestShellDirectory}\Start\Sims 4 Mod Sieve"; Filename: "{app}\Sims4ModSieve.exe"; Tasks: startmenu
Name: "{#TestShellDirectory}\Desktop\Sims 4 Mod Sieve"; Filename: "{app}\Sims4ModSieve.exe"; Tasks: desktopicon
#else
Name: "{autoprograms}\Sims 4 Mod Sieve"; Filename: "{app}\Sims4ModSieve.exe"; Tasks: startmenu
Name: "{autodesktop}\Sims 4 Mod Sieve"; Filename: "{app}\Sims4ModSieve.exe"; Tasks: desktopicon
#endif

[Run]
#ifndef TestDataDirectory
Filename: "{app}\Sims4ModSieve.exe"; Description: "立即运行 Sims 4 Mod Sieve"; Flags: nowait postinstall skipifsilent; BeforeInstall: ReleaseDataLock
Filename: "{app}\Sims4ModSieve.exe"; Flags: nowait; Check: IsApplicationUpdate; BeforeInstall: ReleaseDataLock
#endif

[Code]
const
  InvalidHandle = -1;
var
  DataLock: THandle;
  UpdatingProcess: THandle;
  HelperReady: Boolean;
  RemoveUserData: Boolean;
  PreserveDisabledRecords: Boolean;
  DisabledFolders: TStringList;
  DisabledFolderList: TNewListBox;
  DisabledCancelButton: TNewButton;
  DisabledWarningForm: TSetupForm;

function CreateFileW(FileName: String; Access, Share: Cardinal; Security: NativeUInt;
  Creation, Flags: Cardinal; Template: THandle): THandle;
  external 'CreateFileW@kernel32.dll stdcall';
function CloseHandle(Handle: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';
function GetFileAttributesW(FileName: String): Cardinal;
  external 'GetFileAttributesW@kernel32.dll stdcall';
function OpenProcess(Access: Cardinal; Inherit: Boolean; ProcessId: Cardinal): THandle;
  external 'OpenProcess@kernel32.dll stdcall';
function WaitForSingleObject(Handle: THandle; Milliseconds: Cardinal): Cardinal;
  external 'WaitForSingleObject@kernel32.dll stdcall';

function DataDirectory: String;
begin
#ifdef TestDataDirectory
  Result := '{#TestDataDirectory}';
#else
  Result := ExpandConstant('{localappdata}\Sims4ModSieve');
#endif
end;

function DocumentsDirectory: String;
begin
#ifdef TestDocumentsDirectory
  Result := '{#TestDocumentsDirectory}';
#else
  Result := ExpandConstant('{userdocs}');
#endif
end;

function IsApplicationUpdate: Boolean;
begin
  Result := ExpandConstant('{param:SIEVEHANDOFF|}') <> '';
end;

procedure ReleaseDataLock;
begin
  if (DataLock <> 0) and (DataLock <> InvalidHandle) then CloseHandle(DataLock);
  DataLock := 0;
end;

function AcquireDataLock(Silent: Boolean): Boolean;
var
  Attempt: Integer;
begin
  Result := False;
  if (DataLock <> 0) and (DataLock <> InvalidHandle) then begin Result := True; exit; end;
  if DirExists(DataDirectory) and ((GetFileAttributesW(DataDirectory) and $400) <> 0) then exit;
  if FileExists(AddBackslash(DataDirectory) + 'application.lock') and
    ((GetFileAttributesW(AddBackslash(DataDirectory) + 'application.lock') and $400) <> 0) then exit;
  if not ForceDirectories(DataDirectory) then exit;
  Attempt := 0;
  repeat
    DataLock := CreateFileW(AddBackslash(DataDirectory) + 'application.lock', $C0000000, 0, 0, 4, $80, 0);
    if DataLock <> InvalidHandle then begin Result := True; exit; end;
    if IsApplicationUpdate and (Attempt < 600) then begin
      Sleep(100);
      Attempt := Attempt + 1;
    end else begin
      if Silent then exit;
      if MsgBox('请先正常关闭 Sims 4 Mod Sieve，再继续。正在进行的操作需要完成后才能关闭。',
        mbInformation, MB_RETRYCANCEL) <> IDRETRY then exit;
    end;
  until False;
end;

procedure PrepareHelper;
begin
  if HelperReady then exit;
  ExtractTemporaryFile('Sims4ModSieve.exe');
  ExtractTemporaryFile('D3DCompiler_47_cor3.dll');
  ExtractTemporaryFile('PenImc_cor3.dll');
  ExtractTemporaryFile('PresentationNative_cor3.dll');
  ExtractTemporaryFile('vcruntime140_cor3.dll');
  ExtractTemporaryFile('wpfgfx_cor3.dll');
  HelperReady := True;
end;

function CheckSelectedPath: String;
var
  Code: Integer;
begin
  Result := '无法检查安装位置，请检查本机设置和目录权限后重试。';
  PrepareHelper;
  if not Exec(ExpandConstant('{tmp}\Sims4ModSieve.exe'), '--installer-check-path ' +
    AddQuotes(ExpandConstant('{app}')) + ' ' + AddQuotes(DataDirectory) + ' ' + AddQuotes(DocumentsDirectory),
    ExpandConstant('{tmp}'), SW_HIDE, ewWaitUntilTerminated, Code) then exit;
  if Code = 0 then Result := ''
  else if Code = 10 then Result := '不能安装到游戏用户目录、Mods、Tray 或工具的数据目录。请选择其他位置。';
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Reason: String;
begin
  Result := True;
  if CurPageID = wpSelectDir then begin
    Reason := CheckSelectedPath;
    Result := Reason = '';
    if not Result then SuppressibleMsgBox(Reason, mbError, MB_OK, IDOK);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Handoff: String;
  Acceptance: AnsiString;
  Attempt: Integer;
  ProcessId: Integer;
begin
  Result := CheckSelectedPath;
  if Result <> '' then exit;
  Handoff := ExpandConstant('{param:SIEVEHANDOFF|}');
  if Handoff <> '' then begin
    ProcessId := StrToIntDef(ExpandConstant('{param:SIEVEWAITPID|}'), 0);
    if UpdatingProcess = 0 then begin
      if ProcessId > 0 then UpdatingProcess := OpenProcess($100000, False, ProcessId);
      if UpdatingProcess = 0 then begin
        Result := '无法确认待更新的程序进程，请重试。';
        exit;
      end;
    end;
    if not SaveStringToFile(Handoff, '{#AppVersion}', False) then begin
      Result := '无法交接更新，请重新下载后重试。';
      exit;
    end;
    Attempt := 0;
    while not FileExists(Handoff + '.accepted') do begin
      if Attempt >= 600 then begin
        Result := '工具未确认更新交接，安装已停止。';
        exit;
      end;
      Sleep(100);
      Attempt := Attempt + 1;
    end;
    if not LoadStringFromFile(Handoff + '.accepted', Acceptance) or (Acceptance <> '{#AppVersion}') then begin
      Result := '更新交接确认无效，安装已停止。';
      exit;
    end;
    DeleteFile(Handoff + '.accepted');
    // OnExit 释放运行锁后，进程仍可能持有 EXE 的映射。
    if WaitForSingleObject(UpdatingProcess, 60000) <> 0 then begin
      Result := '工具尚未完全退出，安装已停止。请正常关闭工具后重试。';
      exit;
    end;
  end;
  if not AcquireDataLock(WizardSilent) then begin
    Result := '无法取得程序运行锁，请正常关闭工具后重试。';
    exit;
  end;
  // 取得运行锁后重查配置，覆盖向导打开期间的设置变化。
  Result := CheckSelectedPath;
end;

procedure DeinitializeSetup;
begin
  ReleaseDataLock;
  if UpdatingProcess <> 0 then CloseHandle(UpdatingProcess);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Key: String;
begin
  if CurStep = ssPostInstall then begin
    Key := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\' + ExpandConstant('{#emit SetupSetting("AppId")}_is1');
    // /SILENT 跳过 Inno 自带确认，自定义交互仍由专用标记启用。
    if not RegWriteStringValue(HKCU, Key, 'UninstallString',
      AddQuotes(ExpandConstant('{uninstallexe}')) + ' /SILENT /SIEVEINTERACTIVE=1') then
      RaiseException('无法登记卸载入口。');
  end;
end;

function ConfirmUninstallOptions: Boolean;
var
  Form: TSetupForm;
  Heading, Description: TNewStaticText;
  ClearData: TNewCheckBox;
  UninstallButton, CancelButton: TNewButton;
begin
  Form := CreateCustomForm(520, 205, False, True);
  try
    Form.Caption := '卸载 Sims 4 Mod Sieve';
    Form.ClientWidth := ScaleX(520);
    Form.ClientHeight := ScaleY(205);
    Form.BorderStyle := bsDialog;
    Form.Position := poScreenCenter;
    Form.Color := $F2F5F6;
    Form.Font.Name := 'Microsoft YaHei UI';
    Form.Font.Size := 10;
    Heading := TNewStaticText.Create(Form);
    Heading.Parent := Form;
    Heading.SetBounds(ScaleX(22), ScaleY(20), ScaleX(476), ScaleY(26));
    Heading.Caption := '确定要卸载 Sims 4 Mod Sieve 吗？';
    Heading.Font.Style := [fsBold];
    Description := TNewStaticText.Create(Form);
    Description.Parent := Form;
    Description.AutoSize := False;
    Description.WordWrap := True;
    Description.SetBounds(ScaleX(22), ScaleY(56), ScaleX(476), ScaleY(42));
    Description.Caption := '卸载会删除程序文件。你的 Mods、存档和游戏文件不会被删除。';
    ClearData := TNewCheckBox.Create(Form);
    ClearData.Parent := Form;
    ClearData.SetBounds(ScaleX(22), ScaleY(109), ScaleX(476), ScaleY(28));
    ClearData.Caption := '同时删除本工具的设置、忽略名单、删除记录和日志';
    ClearData.Checked := False;
    CancelButton := TNewButton.Create(Form);
    CancelButton.Parent := Form;
    CancelButton.SetBounds(ScaleX(282), ScaleY(158), ScaleX(100), ScaleY(30));
    CancelButton.Caption := '取消';
    CancelButton.ModalResult := mrCancel;
    CancelButton.Cancel := True;
    CancelButton.Default := True;
    UninstallButton := TNewButton.Create(Form);
    UninstallButton.Parent := Form;
    UninstallButton.SetBounds(ScaleX(394), ScaleY(158), ScaleX(104), ScaleY(30));
    UninstallButton.Caption := '卸载';
    UninstallButton.ModalResult := mrOK;
    Form.ActiveControl := CancelButton;
    Result := Form.ShowModal = mrOK;
    if Result then RemoveUserData := ClearData.Checked;
  finally
    Form.Free;
  end;
end;

procedure OpenDisabledFolder(Sender: TObject);
var
  Code: Integer;
  Folder: String;
begin
  if DisabledFolderList.ItemIndex < 0 then exit;
  Folder := DisabledFolders[DisabledFolderList.ItemIndex];
  if not ShellExec('open', Folder, '', '', SW_SHOWNORMAL, ewNoWait, Code) then
    MsgBox('无法打开这个文件夹，请检查路径是否仍然存在。', mbError, MB_OK)
  else Log('Opened disabled Mod folder: ' + Folder);
  DisabledWarningForm.ActiveControl := DisabledCancelButton;
end;

procedure SelectDisabledFolder(Sender: TObject);
begin
  if DisabledFolderList.ItemIndex >= 0 then
    DisabledFolderList.Hint := DisabledFolders[DisabledFolderList.ItemIndex];
end;

function ConfirmDisabledMods(Count: Integer): Boolean;
var
  Form: TSetupForm;
  Description, DangerLabel: TNewStaticText;
  OpenButton, CancelButton, ContinueButton: TNewButton;
begin
  Form := CreateCustomForm(620, 328, False, True);
  DisabledWarningForm := Form;
  try
    Form.Caption := '仍有禁用的 Mod';
    Form.ClientWidth := ScaleX(620);
    Form.ClientHeight := ScaleY(328);
    Form.BorderStyle := bsDialog;
    Form.Position := poScreenCenter;
    Form.Color := $F2F5F6;
    Form.Font.Name := 'Microsoft YaHei UI';
    Form.Font.Size := 10;
    Description := TNewStaticText.Create(Form);
    Description.Parent := Form;
    Description.AutoSize := False;
    Description.WordWrap := True;
    Description.SetBounds(ScaleX(22), ScaleY(20), ScaleX(576), ScaleY(70));
    Description.Font.Color := $3333B2;
    Description.Font.Style := [fsBold];
    Description.Caption := Format('还有 %d 个 Mod 处于禁用状态。卸载后工具就不能帮你恢复它们，需要自己手动去掉 .off 后缀恢复。', [Count]);
    DisabledFolderList := TNewListBox.Create(Form);
    DisabledFolderList.Parent := Form;
    DisabledFolderList.SetBounds(ScaleX(22), ScaleY(104), ScaleX(576), ScaleY(115));
    DisabledFolderList.Items.Assign(DisabledFolders);
    DisabledFolderList.ItemIndex := 0;
    DisabledFolderList.ShowHint := True;
    DisabledFolderList.OnClick := @SelectDisabledFolder;
    SelectDisabledFolder(DisabledFolderList);
    OpenButton := TNewButton.Create(Form);
    OpenButton.Parent := Form;
    OpenButton.SetBounds(ScaleX(22), ScaleY(230), ScaleX(118), ScaleY(30));
    OpenButton.Caption := '打开文件夹';
    OpenButton.OnClick := @OpenDisabledFolder;
    CancelButton := TNewButton.Create(Form);
    CancelButton.Parent := Form;
    CancelButton.SetBounds(ScaleX(338), ScaleY(278), ScaleX(120), ScaleY(30));
    CancelButton.Caption := '取消卸载';
    CancelButton.ModalResult := mrCancel;
    CancelButton.Default := True;
    CancelButton.Cancel := True;
    DisabledCancelButton := CancelButton;
    DangerLabel := TNewStaticText.Create(Form);
    DangerLabel.Parent := Form;
    DangerLabel.AutoSize := False;
    DangerLabel.SetBounds(ScaleX(470), ScaleY(258), ScaleX(128), ScaleY(18));
    DangerLabel.Alignment := taCenter;
    DangerLabel.Font.Color := $3333B2;
    DangerLabel.Caption := '危险操作';
    ContinueButton := TNewButton.Create(Form);
    ContinueButton.Parent := Form;
    ContinueButton.SetBounds(ScaleX(470), ScaleY(278), ScaleX(128), ScaleY(30));
    ContinueButton.Caption := '仍然卸载';
    ContinueButton.Font.Style := [fsBold];
    ContinueButton.ModalResult := mrOK;
    Form.ActiveControl := CancelButton;
    Result := Form.ShowModal = mrOK;
  finally
    Form.Free;
  end;
end;

function InitializeUninstall: Boolean;
var
  Code, Count, FolderCount, Index: Integer;
  Reason, ReportPath, Folder: String;
  Interactive: Boolean;
begin
  Result := False;
  if not UninstallSilent then begin
    MsgBox('请从 Windows「设置 → 应用」卸载 Sims 4 Mod Sieve，以选择是否删除本机数据。', mbInformation, MB_OK);
    exit;
  end;
  Interactive := ExpandConstant('{param:SIEVEINTERACTIVE|0}') = '1';
  if not AcquireDataLock(not Interactive) then exit;
  ReportPath := ExpandConstant('{tmp}\sieve-uninstall.ini');
  Reason := '无法读取禁用记录，请先打开 Sims 4 Mod Sieve 检查后再卸载。';
  if Exec(ExpandConstant('{app}\Sims4ModSieve.exe'), '--installer-check-uninstall ' + AddQuotes(DataDirectory) + ' ' + AddQuotes(ReportPath),
    ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, Code) and ((Code = 0) or (Code = 20)) then begin
    Count := StrToIntDef(GetIniString('Uninstall', 'DisabledCount', '-1', ReportPath), -1);
    FolderCount := StrToIntDef(GetIniString('Uninstall', 'FolderCount', '-1', ReportPath), -1);
    Result := (Count >= 0) and (FolderCount >= 0) and (FolderCount <= Count)
      and (((Code = 0) and (Count = 0)) or ((Code = 20) and (Count > 0) and (FolderCount > 0)));
    if Result then begin
      DisabledFolders := TStringList.Create;
      for Index := 0 to FolderCount - 1 do begin
        Folder := GetIniString('Uninstall', 'Folder' + IntToStr(Index), '', ReportPath);
        if Folder = '' then Result := False else DisabledFolders.Add(Folder);
      end;
    end;
  end;
  if not Result then begin
    Log(Reason);
    if Interactive then MsgBox(Reason, mbError, MB_OK);
    ReleaseDataLock;
    exit;
  end;
  RemoveUserData := False;
  PreserveDisabledRecords := False;
  if Interactive then Result := ConfirmUninstallOptions;
  if Result and (Count > 0) then begin
    if Interactive then begin
      Result := ConfirmDisabledMods(Count);
      PreserveDisabledRecords := Result;
    end else begin
      Log('Unresolved disabled Mods prevent silent uninstall.');
      Result := False;
    end;
  end;
  if not Result then ReleaseDataLock;
end;

function ClearDataDirectory(Directory: String): Boolean;
var
  Entry: TFindRec;
  Path: String;
begin
  Result := True;
  if not DirExists(Directory) then exit;
  if FindFirst(AddBackslash(Directory) + '*', Entry) then begin
    try
      repeat
        if (Entry.Name <> '.') and (Entry.Name <> '..') and (Entry.Name <> 'application.lock')
          and not (PreserveDisabledRecords and (Directory = DataDirectory) and (CompareText(Entry.Name, 'disabled-mods.json') = 0)) then begin
          Path := AddBackslash(Directory) + Entry.Name;
          // 不跟随目录链接，避免清理跳出本机数据目录。
          if (Entry.Attributes and $400) <> 0 then Result := False
          else if (Entry.Attributes and $10) <> 0 then begin
            if not ClearDataDirectory(Path) then Result := False;
            if not RemoveDir(Path) then Result := False;
          end else if not DeleteFile(Path) then Result := False;
        end;
      until not FindNext(Entry);
    finally
      FindClose(Entry);
    end;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Cleared: Boolean;
begin
  if (CurUninstallStep = usPostUninstall) and RemoveUserData then begin
    Cleared := ClearDataDirectory(DataDirectory);
    ReleaseDataLock;
    if not DeleteFile(AddBackslash(DataDirectory) + 'application.lock') then Cleared := False;
    if not PreserveDisabledRecords and not RemoveDir(DataDirectory) then Cleared := False;
    if not Cleared then MsgBox('部分本机数据未能删除，请检查目录权限或目录链接。', mbInformation, MB_OK);
  end;
end;

procedure DeinitializeUninstall;
begin
  ReleaseDataLock;
  if DisabledFolders <> nil then DisabledFolders.Free;
end;
