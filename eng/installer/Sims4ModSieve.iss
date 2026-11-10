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
SetupIconFile=..\..\src\Sims4ModSieve.Desktop\Assets\Sims4ModSieve.ico
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

function InitializeUninstall: Boolean;
var
  Code: Integer;
  Reason: String;
begin
  Result := False;
  if not AcquireDataLock(UninstallSilent) then exit;
  Reason := '无法读取禁用记录，请先打开 Sims 4 Mod Sieve 检查后再卸载。';
  if Exec(ExpandConstant('{app}\Sims4ModSieve.exe'), '--installer-check-uninstall ' + AddQuotes(DataDirectory),
    ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, Code) then begin
    if Code = 0 then Result := True
    else if Code = 20 then Reason := '还有未处理的禁用 Mod，请先在「设置 → 删除与忽略 → 已禁用」中处理，再卸载。';
  end;
  if not Result then begin
    Log(Reason);
    if not UninstallSilent then MsgBox(Reason, mbError, MB_OK);
    ReleaseDataLock;
    exit;
  end;
  RemoveUserData := False;
  if not UninstallSilent then
    RemoveUserData := MsgBox('是否同时删除本机数据？设置、忽略名单、工具删除记录和日志会一并删除。',
      mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
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
        if (Entry.Name <> '.') and (Entry.Name <> '..') and (Entry.Name <> 'application.lock') then begin
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
    if not RemoveDir(DataDirectory) then Cleared := False;
    if not Cleared then MsgBox('部分本机数据未能删除，请检查目录权限或目录链接。', mbInformation, MB_OK);
  end;
end;

procedure DeinitializeUninstall;
begin
  ReleaseDataLock;
end;
