; One payload for a registered per-user install or an unregistered portable folder.
#ifndef ReleaseDir
  #error Supply /DReleaseDir with a clean publish.ps1 output directory.
#endif
#ifndef AppVersion
  #define AppVersion "1.3.0_fork.3"
#endif
#ifndef AppFileVersion
  #define AppFileVersion "1.3.0.3"
#endif
#define AppId "{68F68AD3-6E07-495D-98CD-CFC119CCB43D}"

[Setup]
AppId={{68F68AD3-6E07-495D-98CD-CFC119CCB43D}
AppName=NAI Utility Tool
AppVersion={#AppVersion}
AppPublisher=Andy-linux / Aeka0
AppPublisherURL=https://github.com/Andy-linux/NAI-Utility-Tool
AppSupportURL=https://github.com/Andy-linux/NAI-Utility-Tool/issues
DefaultDirName={code:GetDefaultDir}
DisableDirPage=no
AppendDefaultDirName=no
UsePreviousAppDir=no
UsePreviousTasks=no
DefaultGroupName=NAI Utility Tool
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
Uninstallable=not IsPortable
CreateUninstallRegKey=not IsPortable
UninstallFilesDir={app}\uninstall
UninstallDisplayIcon={app}\NAI Utility Tool.exe
CloseApplications=no
RestartApplications=no
WizardStyle=modern
SetupIconFile=..\assets\icon\NAIT.ico
LicenseFile={#ReleaseDir}\LICENSE
OutputBaseFilename=NAI.Utility.Tool.{#AppVersion}.Setup
Compression=lzma2
SolidCompression=yes
VersionInfoVersion={#AppFileVersion}
VersionInfoTextVersion={#AppVersion}

[Languages]
Name: "zh_cn"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
zh_cn.ModeTitle=选择安装方式
zh_cn.ModeDescription=全新安装或覆盖升级已有目录
zh_cn.ModePrompt=覆盖升级旧版本时，下一页请选择包含启动器和 bin 文件夹的旧版根目录。程序会替换，用户数据保留。
zh_cn.InstallMode=安装版：当前用户的开始菜单和卸载入口
zh_cn.PortableMode=便携版：仅写入所选目录，不创建快捷方式或卸载登记
zh_cn.DesktopIcon=创建桌面快捷方式
zh_cn.CloseApplication=请先保存并关闭目标目录中的 NAI Utility Tool，再重试。目标程序文件正在使用，或当前用户没有写入权限。
zh_cn.SelectRoot=请选择包含启动器和 bin 文件夹的根目录，不要选择 bin 子目录。
zh_cn.KeepInstalledMode=该目录已有安装版。请使用安装版覆盖升级，或为便携版选择其他目录。
en.ModeTitle=Installation mode
en.ModeDescription=New installation or in-place upgrade
en.ModePrompt=To upgrade an older build, select its existing root folder on the next page (the folder containing the launcher and bin). Program files are replaced; user data is kept.
en.InstallMode=Installed: current-user Start menu and uninstall entry
en.PortableMode=Portable: selected folder only, no shortcuts or uninstall registration
en.DesktopIcon=Create a desktop shortcut
en.CloseApplication=Save your work and close NAI Utility Tool in the destination folder, then retry. The application file is in use or the folder is not writable by the current user.
en.SelectRoot=Select the root folder containing the launcher and bin, not the bin subfolder.
en.KeepInstalledMode=This folder already has an installed edition. Upgrade it in installed mode, or choose another folder for portable mode.

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopIcon}"; Flags: unchecked; Check: not IsPortable

[Files]
; Replace program binaries; preserve user-owned data and seed resources below.
Source: "{#ReleaseDir}\NAI Utility Tool.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#ReleaseDir}\bin\*"; DestDir: "{app}\bin"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#ReleaseDir}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#ReleaseDir}\INSTALL.txt"; DestDir: "{app}"; Flags: ignoreversion
; Seed resources may have been customized. Add missing files; never overwrite or uninstall them.
Source: "{#ReleaseDir}\assets\*"; DestDir: "{app}\assets"; Flags: onlyifdoesntexist uninsneveruninstall recursesubdirs createallsubdirs
Source: "{#ReleaseDir}\models\*"; DestDir: "{app}\models"; Flags: onlyifdoesntexist uninsneveruninstall recursesubdirs createallsubdirs
; user/, output/ and logs/ are created by the application, not owned by Setup.

[Icons]
Name: "{userprograms}\NAI Utility Tool"; Filename: "{app}\NAI Utility Tool.exe"; WorkingDir: "{app}"; Check: not IsPortable
Name: "{userdesktop}\NAI Utility Tool"; Filename: "{app}\NAI Utility Tool.exe"; WorkingDir: "{app}"; Tasks: desktopicon; Check: not IsPortable

[Run]
Filename: "{app}\NAI Utility Tool.exe"; Description: "{cm:LaunchProgram,NAI Utility Tool}"; Flags: nowait postinstall skipifsilent unchecked

[Code]
var
  ModePage: TInputOptionWizardPage;
  PreviousPortable: Boolean;

function OpenProcess(Access: LongWord; InheritHandle: Boolean; ProcessId: LongWord): THandle;
  external 'OpenProcess@kernel32.dll stdcall';
function WaitForSingleObject(Handle: THandle; Milliseconds: LongWord): LongWord;
  external 'WaitForSingleObject@kernel32.dll stdcall';
function CloseHandle(Handle: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';

function InitializeSetup: Boolean;
var
  ParentId: Integer;
  ParentHandle: THandle;
begin
  Result := True;
  ParentId := StrToIntDef(ExpandConstant('{param:UPDATEPID|0}'), 0);
  if ParentId <= 0 then Exit;
  // The client launches Setup only after its normal close checks pass.
  // Wait for settings to be saved and the assembly to be released before showing Setup.
  ParentHandle := OpenProcess($00100000, False, ParentId);
  if ParentHandle = 0 then Exit;
  Result := WaitForSingleObject(ParentHandle, 30000) = 0;
  CloseHandle(ParentHandle);
  if not Result then
    SuppressibleMsgBox(CustomMessage('CloseApplication'), mbError, MB_OK, IDOK);
end;

function IsPortable: Boolean;
begin
  Result := ExpandConstant('{param:PORTABLE|0}') = '1';
  if ModePage <> nil then
    Result := ModePage.SelectedValueIndex = 1;
end;

function GetDefaultDir(Param: String): String;
begin
  if IsPortable then begin
    Result := ExpandConstant('{src}\NAI Utility Tool');
    if FileExists(ExpandConstant('{src}\bin\NAI Utility Tool.exe')) then
      Result := ExpandConstant('{src}');
  end else if not RegQueryStringValue(HKCU,
    'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#AppId}_is1',
    'InstallLocation', Result) then
    Result := ExpandConstant('{localappdata}\Programs\NAI Utility Tool');
end;

procedure InitializeWizard;
begin
  PreviousPortable := IsPortable;
  ModePage := CreateInputOptionPage(wpWelcome, CustomMessage('ModeTitle'),
    CustomMessage('ModeDescription'), CustomMessage('ModePrompt'), True, False);
  ModePage.Add(CustomMessage('InstallMode'));
  ModePage.Add(CustomMessage('PortableMode'));
  if PreviousPortable then ModePage.SelectedValueIndex := 1
  else ModePage.SelectedValueIndex := 0;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = ModePage.ID) and (PreviousPortable <> IsPortable) then begin
    if ExpandConstant('{param:DIR|}') = '' then
      WizardForm.DirEdit.Text := GetDefaultDir('');
    PreviousPortable := IsPortable;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Target: String;
  Probe: TFileStream;
begin
  Result := '';
  Target := AddBackslash(ExpandConstant('{app}'));
  if FileExists(Target + 'NAI Utility Tool.dll') then begin
    Result := CustomMessage('SelectRoot');
    Exit;
  end;
  if IsPortable and FileExists(Target + 'uninstall\unins000.exe') then begin
    Result := CustomMessage('KeepInstalledMode');
    Exit;
  end;
  // Check the main assembly before copying any files; never terminate a user's session.
  if FileExists(Target + 'bin\NAI Utility Tool.dll') then begin
    try
      Probe := TFileStream.Create(Target + 'bin\NAI Utility Tool.dll', fmOpenReadWrite or fmShareExclusive);
      Probe.Free;
    except
      Result := CustomMessage('CloseApplication');
    end;
  end;
end;
