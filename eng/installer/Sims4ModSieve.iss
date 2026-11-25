#ifndef AppVersion
  #error AppVersion must come from the project.
#endif
#ifndef SourceDirectory
  #error SourceDirectory is required.
#endif
#ifndef OutputDirectory
  #error OutputDirectory is required.
#endif
#ifdef TestDefaultDirectory
  #define SieveDefaultDirectory TestDefaultDirectory
#else
  #define SieveDefaultDirectory "{localappdata}\Programs\Sims4ModSieve"
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
DefaultDirName={#SieveDefaultDirectory}
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

[CustomMessages]
PathCheckTitle=未能完成安装位置检查
PathCheckExtractFailed=安装位置检查程序未能解包。
PathCheckStartFailed=安装位置检查程序未能启动。
PathCheckBlocked=安装位置检查程序未能启动，可能被安全软件拦截。
PathCheckExited=安装位置检查程序意外退出，未返回检查结果。
PathCheckNoReason=检查程序未能确认所选位置的实际目录。
PathCheckLog=检查日志保存在 %1
PathCheckLogFailed=检查日志未能保存。
PathCheckChooseAnother=换个位置
PathCheckContinue=仍然安装
PathCheckGoBack=安装尚未开始。请返回「选择目标位置」换个位置。

[Tasks]
Name: "startmenu"; Description: "创建开始菜单快捷方式"
Name: "desktopicon"; Description: "创建桌面快捷方式"; Flags: unchecked

[Files]
Source: "{#SourceDirectory}\Sims4ModSieve.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDirectory}\Sims4ModSieve.Cli.exe"; DestDir: "{app}"; Flags: ignoreversion
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
  PathUnsafe = 10;
  PathUnknown = 11;
  UnsafePathMessage = '不能安装到游戏用户目录、Mods、Tray 或工具的数据目录。请选择其他位置。';
var
  DataLock: THandle;
  UpdatingProcess: THandle;
  HelperReady: Boolean;
  PathCheckNumber: Integer;
  AcceptedInstallPath: String;
  RemoveUserData: Boolean;
  PreserveDisabledRecords: Boolean;
  DisabledFolders: TStringList;
  DisabledFolderList: TNewListBox;
  DisabledCancelButton: TNewButton;
  DisabledWarningForm: TSetupForm;
  UninstallLargeIcon, UninstallSmallIcon: THandle;

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
function ExtractIconExW(FileName: String; IconIndex: Integer; var LargeIcon, SmallIcon: THandle; Count: Cardinal): Cardinal;
  external 'ExtractIconExW@shell32.dll stdcall';
function DestroyIcon(Icon: THandle): Boolean;
  external 'DestroyIcon@user32.dll stdcall';

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

function WritePathCheckLog(const Lines: TArrayOfString): String;
var
  Data, Candidate: String;
  Attributes: Cardinal;
  I: Integer;
begin
  Result := '';
  for I := 0 to GetArrayLength(Lines) - 1 do Log(Lines[I]);
  Data := DataDirectory;
  Attributes := GetFileAttributesW(Data);
  if ((Attributes = $FFFFFFFF) or ((Attributes and $400) = 0)) and ForceDirectories(Data) then begin
    Candidate := AddBackslash(Data) + 'installer-check.log';
    Attributes := GetFileAttributesW(Candidate);
    if ((Attributes = $FFFFFFFF) or ((Attributes and $400) = 0)) and
      SaveStringsToUTF8File(Candidate, Lines, True) then begin
      Result := Candidate;
      exit;
    end;
  end;
  Candidate := AddBackslash(ExtractFileDir(Data)) + 'Sims4ModSieve-installer-check.log';
  Attributes := GetFileAttributesW(Candidate);
  if ((Attributes = $FFFFFFFF) or ((Attributes and $400) = 0)) and
    SaveStringsToUTF8File(Candidate, Lines, True) then Result := Candidate;
end;

function PathCheckArgument(Value: String): String;
begin
  Value := RemoveBackslashUnlessRoot(Value);
  if (Length(Value) > 0) and (Value[Length(Value)] = '\') then Value := Value + '\';
  Result := '"' + Value + '"';
end;

function CheckSelectedPath(const Stage, Target: String; ContinueIfUnknown: Boolean; var Reason, LogPath: String): Integer;
var
  Code, EntryCount, I: Integer;
  Helper, Arguments, ReportPath, Documents, Detail, PathToCheck: String;
  Lines: TArrayOfString;
#ifdef TestDataDirectory
  TestFailure: String;
#endif
begin
  Result := PathUnknown;
  Reason := '';
  EntryCount := 0;
  Documents := '';
  try
    Documents := DocumentsDirectory;
  except
    Detail := '无法取得 Windows 文档目录：' + GetExceptionMessage;
  end;
  PathCheckNumber := PathCheckNumber + 1;
  ReportPath := ExpandConstant('{tmp}\sieve-install-path-') + IntToStr(PathCheckNumber) + '.ini';
  Helper := ExpandConstant('{tmp}\Sims4ModSieve.exe');
  PathToCheck := Target;
#ifdef TestDataDirectory
  TestFailure := ExpandConstant('{param:SIEVETESTPATHCHECK|}');
  if (TestFailure = 'late-target') and (DataLock <> 0) then PathToCheck := 'relative';
#endif
  try
#ifdef TestDataDirectory
    if TestFailure = 'extract' then ExtractTemporaryFile('missing-test-helper.exe');
#endif
    PrepareHelper;
  except
    Reason := CustomMessage('PathCheckExtractFailed');
    Detail := Detail + ' 解包失败：' + GetExceptionMessage;
  end;
  if Reason = '' then begin
    Arguments := '--installer-check-path ' + PathCheckArgument(PathToCheck) + ' ' + PathCheckArgument(DataDirectory) +
      ' ' + PathCheckArgument(Documents) + ' ' + PathCheckArgument(ReportPath);
#ifdef TestDataDirectory
    if TestFailure = 'start' then
      Helper := ExpandConstant('{tmp}\missing-test-helper.exe');
    if TestFailure = 'exit' then begin
      Helper := ExpandConstant('{cmd}');
      Arguments := '/D /C exit 23';
    end;
#endif
    if Exec(Helper, Arguments, ExpandConstant('{tmp}'), SW_HIDE, ewWaitUntilTerminated, Code) then begin
      Detail := Detail + ' 检查程序退出码：' + IntToStr(Code);
      if (Code = 0) or (Code = PathUnsafe) or (Code = PathUnknown) then begin
        Result := Code;
        EntryCount := StrToIntDef(GetIniString('PathCheck', 'EntryCount', '-1', ReportPath), -1);
        if (EntryCount < 0) or (EntryCount > 32) then begin
          Detail := Detail + '。未读到有效的检查报告';
          EntryCount := 0;
        end;
        if Code = PathUnknown then Reason := GetIniString('PathCheck', 'Reason', CustomMessage('PathCheckNoReason'), ReportPath);
      end else Reason := CustomMessage('PathCheckExited');
    end else begin
      if (Code = 225) or (Code = 226) or (Code = 1260) then Reason := CustomMessage('PathCheckBlocked')
      else Reason := CustomMessage('PathCheckStartFailed');
      Detail := Detail + ' 启动失败，Windows 错误 ' + IntToStr(Code) + '：' + SysErrorMessage(Code);
    end;
  end;
  SetArrayLength(Lines, 4 + EntryCount);
  Lines[0] := '';
  Lines[1] := GetDateTimeString('yyyy-mm-dd hh:nn:ss', '-', ':') + ' ' + Stage + '，安装位置：' + Target;
  Lines[2] := Trim(Detail);
  for I := 0 to EntryCount - 1 do
    Lines[3 + I] := GetIniString('PathCheck', 'Entry' + IntToStr(I), '', ReportPath);
  case Result of
    0: Lines[3 + EntryCount] := '路径检查通过';
    PathUnsafe: Lines[3 + EntryCount] := '拦截：' + UnsafePathMessage;
    PathUnknown: begin
      Lines[3 + EntryCount] := '未完成检查：' + Reason;
      if ContinueIfUnknown then Lines[3 + EntryCount] := Lines[3 + EntryCount] + ' 继续安装。';
    end;
  end;
  LogPath := WritePathCheckLog(Lines);
end;

function SelectedPathBlockReason(const Stage: String): String;
var
  Code: Integer;
  ContinueIfUnknown: Boolean;
  Target, Reason, LogPath, LogNote, Decision: String;
begin
  Result := '';
  Target := RemoveBackslashUnlessRoot(ExpandConstant('{app}'));
  ContinueIfUnknown := WizardSilent or IsApplicationUpdate or
    (CompareText(Target, ExpandConstant('{#SieveDefaultDirectory}')) = 0) or
    (CompareText(Target, AcceptedInstallPath) = 0);
  Code := CheckSelectedPath(Stage, Target, ContinueIfUnknown, Reason, LogPath);
  if Code = PathUnsafe then begin
    Result := UnsafePathMessage;
    exit;
  end;
  if (Code <> PathUnknown) or ContinueIfUnknown then exit;

  if LogPath = '' then LogNote := CustomMessage('PathCheckLogFailed')
  else LogNote := FmtMessage(CustomMessage('PathCheckLog'), [LogPath]);
  if TaskDialogMsgBox(CustomMessage('PathCheckTitle'), Reason + #13#10#13#10 + LogNote, mbInformation,
    MB_YESNO, [CustomMessage('PathCheckChooseAnother'), CustomMessage('PathCheckContinue')], 0) = IDNO then begin
    AcceptedInstallPath := Target;
    Decision := CustomMessage('PathCheckContinue');
  end else begin
    Result := CustomMessage('PathCheckGoBack');
    Decision := CustomMessage('PathCheckChooseAnother');
  end;
  Log('用户选择：' + Decision);
  if LogPath <> '' then SaveStringsToUTF8File(LogPath, ['用户选择：' + Decision], True);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Reason: String;
begin
  Result := True;
  if CurPageID = wpSelectDir then begin
    Reason := SelectedPathBlockReason('选择目标位置');
    Result := Reason = '';
    if Reason = UnsafePathMessage then SuppressibleMsgBox(Reason, mbError, MB_OK, IDOK);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Handoff: String;
  Acceptance: AnsiString;
  Attempt: Integer;
  ProcessId: Integer;
begin
  Result := SelectedPathBlockReason('安装前');
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
  Result := SelectedPathBlockReason('取得运行锁后');
  if Result = CustomMessage('PathCheckGoBack') then ReleaseDataLock;
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

procedure ShowUninstallWindowIcon(Sender: TObject);
var
  Form: TSetupForm;
begin
  Form := TSetupForm(Sender);
  if (UninstallLargeIcon = 0) and (UninstallSmallIcon = 0) then
    ExtractIconExW(ExpandConstant('{app}\Sims4ModSieve.exe'), 0, UninstallLargeIcon, UninstallSmallIcon, 1);
  if UninstallSmallIcon <> 0 then SendMessage(Form.Handle, $0080, 0, UninstallSmallIcon);
  if UninstallLargeIcon <> 0 then SendMessage(Form.Handle, $0080, 1, UninstallLargeIcon);
end;

function ConfirmUninstallOptions: Boolean;
var
  Form: TSetupForm;
  Heading, Description: TNewStaticText;
  ClearData: TNewCheckBox;
  UninstallButton, CancelButton: TNewButton;
  WarningIcon: TBitmapImage;
begin
  Form := CreateCustomForm(380, 168, False, True);
  try
    Form.Caption := '卸载 Sims4ModSieve';
    Form.ClientWidth := ScaleX(380);
    Form.ClientHeight := ScaleY(168);
    Form.BorderStyle := bsSingle;
    Form.BorderIcons := [biSystemMenu];
    Form.OnShow := @ShowUninstallWindowIcon;
    Form.Position := poScreenCenter;
    Form.Color := $F2F5F6;
    Form.Font.Name := 'Microsoft YaHei UI';
    Form.Font.Size := 9;
    WarningIcon := TBitmapImage.Create(Form);
    WarningIcon.Parent := Form;
    WarningIcon.SetBounds(ScaleX(16), ScaleY(16), ScaleX(20), ScaleY(20));
    InitializeBitmapImageFromStockIcon(WarningIcon, SIID_WARNING, clNone, []);
    Heading := TNewStaticText.Create(Form);
    Heading.Parent := Form;
    Heading.SetBounds(ScaleX(46), ScaleY(18), ScaleX(318), ScaleY(24));
    Heading.Caption := '确定要卸载 Sims 4 Mod Sieve 吗？';
    Heading.Font.Style := [fsBold];
    Description := TNewStaticText.Create(Form);
    Description.Parent := Form;
    Description.AutoSize := False;
    Description.WordWrap := True;
    Description.SetBounds(ScaleX(16), ScaleY(48), ScaleX(348), ScaleY(36));
    Description.Caption := '卸载会删除程序文件。你的 Mods、存档和游戏文件不会被删除。';
    ClearData := TNewCheckBox.Create(Form);
    ClearData.Parent := Form;
    ClearData.SetBounds(ScaleX(16), ScaleY(90), ScaleX(348), ScaleY(24));
    ClearData.Caption := '同时删除本工具的设置、忽略名单、删除记录和日志';
    ClearData.Checked := False;
    CancelButton := TNewButton.Create(Form);
    CancelButton.Parent := Form;
    CancelButton.SetBounds(ScaleX(202), ScaleY(126), ScaleX(76), ScaleY(26));
    CancelButton.Caption := '否';
    CancelButton.ModalResult := mrCancel;
    CancelButton.Cancel := True;
    UninstallButton := TNewButton.Create(Form);
    UninstallButton.Parent := Form;
    UninstallButton.SetBounds(ScaleX(288), ScaleY(126), ScaleX(76), ScaleY(26));
    UninstallButton.Caption := '是';
    UninstallButton.ModalResult := mrOK;
    UninstallButton.Default := True;
    Form.ActiveControl := UninstallButton;
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

procedure ContinueDisabledUninstall(Sender: TObject);
begin
  // 脚本未暴露 TForm.ModalResult，用窗体 Tag 记录自绘按钮的明确选择。
  DisabledWarningForm.Tag := mrOK;
  DisabledWarningForm.Close;
end;

function ConfirmDisabledMods(Count: Integer): Boolean;
var
  Form: TSetupForm;
  Description: TNewStaticText;
  CancelButton: TNewButton;
  OpenButton, ContinueButton: TBitmapButton;
begin
  Form := CreateCustomForm(430, 238, False, True);
  DisabledWarningForm := Form;
  try
    Form.Caption := '卸载 Sims4ModSieve';
    Form.ClientWidth := ScaleX(430);
    Form.ClientHeight := ScaleY(238);
    Form.BorderStyle := bsSingle;
    Form.BorderIcons := [biSystemMenu];
    Form.OnShow := @ShowUninstallWindowIcon;
    Form.Position := poScreenCenter;
    Form.Color := $F2F5F6;
    Form.Font.Name := 'Microsoft YaHei UI';
    Form.Font.Size := 9;
    Description := TNewStaticText.Create(Form);
    Description.Parent := Form;
    Description.AutoSize := False;
    Description.WordWrap := True;
    Description.SetBounds(ScaleX(16), ScaleY(16), ScaleX(398), ScaleY(56));
    Description.Font.Color := $3333B2;
    Description.Font.Style := [fsBold];
    Description.Caption := Format('还有 %d 个 Mod 处于禁用状态。卸载后工具就不能帮你恢复它们，需要自己手动去掉 .off 后缀恢复。', [Count]);
    DisabledFolderList := TNewListBox.Create(Form);
    DisabledFolderList.Parent := Form;
    DisabledFolderList.SetBounds(ScaleX(16), ScaleY(80), ScaleX(398), ScaleY(72));
    DisabledFolderList.Items.Assign(DisabledFolders);
    DisabledFolderList.ItemIndex := 0;
    DisabledFolderList.ShowHint := True;
    DisabledFolderList.OnClick := @SelectDisabledFolder;
    SelectDisabledFolder(DisabledFolderList);
    OpenButton := TBitmapButton.Create(Form);
    OpenButton.Parent := Form;
    OpenButton.SetBounds(ScaleX(24), ScaleY(164), ScaleX(80), ScaleY(20));
    OpenButton.Caption := '打开文件夹';
    OpenButton.Cursor := crHand;
    OpenButton.TabStop := True;
    OpenButton.Bitmap.Width := OpenButton.Width - 4;
    OpenButton.Bitmap.Height := OpenButton.Height - 4;
    OpenButton.Bitmap.Canvas.Brush.Color := Form.Color;
    OpenButton.Bitmap.Canvas.Pen.Color := Form.Color;
    OpenButton.Bitmap.Canvas.Rectangle(0, 0, OpenButton.Bitmap.Width, OpenButton.Bitmap.Height);
    OpenButton.Bitmap.Canvas.Font.Assign(Form.Font);
    OpenButton.Bitmap.Canvas.Font.Size := 8;
    OpenButton.Bitmap.Canvas.Font.Color := $625475;
    OpenButton.Bitmap.Canvas.TextOut(0,
      (OpenButton.Bitmap.Height - OpenButton.Bitmap.Canvas.TextHeight(OpenButton.Caption)) div 2, OpenButton.Caption);
    OpenButton.OnClick := @OpenDisabledFolder;
    CancelButton := TNewButton.Create(Form);
    CancelButton.Parent := Form;
    CancelButton.SetBounds(ScaleX(330), ScaleY(194), ScaleX(84), ScaleY(28));
    CancelButton.Caption := '取消卸载';
    CancelButton.ModalResult := mrCancel;
    CancelButton.Default := True;
    CancelButton.Cancel := True;
    CancelButton.TabOrder := 3;
    DisabledCancelButton := CancelButton;
    ContinueButton := TBitmapButton.Create(Form);
    ContinueButton.Parent := Form;
    ContinueButton.SetBounds(ScaleX(234), ScaleY(194), ScaleX(86), ScaleY(28));
    ContinueButton.Caption := '仍然卸载';
    ContinueButton.Cursor := crHand;
    ContinueButton.TabStop := True;
    ContinueButton.TabOrder := 2;
    ContinueButton.BackColor := clWhite;
    ContinueButton.Bitmap.Width := ContinueButton.Width - 4;
    ContinueButton.Bitmap.Height := ContinueButton.Height - 4;
    ContinueButton.Bitmap.Canvas.Brush.Color := clWhite;
    ContinueButton.Bitmap.Canvas.Pen.Color := clWhite;
    ContinueButton.Bitmap.Canvas.Rectangle(0, 0, ContinueButton.Bitmap.Width, ContinueButton.Bitmap.Height);
    ContinueButton.Bitmap.Canvas.Font.Assign(Form.Font);
    ContinueButton.Bitmap.Canvas.Font.Color := $3333B2;
    ContinueButton.Bitmap.Canvas.TextOut(
      (ContinueButton.Bitmap.Width - ContinueButton.Bitmap.Canvas.TextWidth(ContinueButton.Caption)) div 2,
      (ContinueButton.Bitmap.Height - ContinueButton.Bitmap.Canvas.TextHeight(ContinueButton.Caption)) div 2, ContinueButton.Caption);
    ContinueButton.OnClick := @ContinueDisabledUninstall;
    Form.ActiveControl := CancelButton;
    Form.Tag := mrCancel;
    Form.ShowModal;
    Result := Form.Tag = mrOK;
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
  if UninstallSmallIcon <> 0 then DestroyIcon(UninstallSmallIcon);
  if UninstallLargeIcon <> 0 then DestroyIcon(UninstallLargeIcon);
end;
