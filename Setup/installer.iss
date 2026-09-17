; DSH Launcher 安装包脚本(Inno Setup 7,兼容 6)
; 使用前先发布应用:
;   dotnet publish "DSH Launcher\DSH Launcher.csproj" -p:PublishProfile="DSH Launcher_Windows_x64" -c Release
; 然后用 Inno Setup Compiler 打开本脚本编译,输出本目录(Setup\)下的 DSHLauncher-Setup-x64.exe
; 安装程序会检测 .NET 10 Desktop Runtime,缺失时引导用户到官网下载。

#define MyAppName "DSH Launcher"
#define MyAppNameNoSpace "DSHLauncher"
#define MyAppPublisher "DSH Launcher"
#define MyAppExeName "DSH Launcher.exe"
#define MyPublishDir "..\DSH Launcher\bin\Publish\DSH Launcher_Windows_x64"
#define MyAppVersion GetVersionNumbersString(MyPublishDir + "\" + MyAppExeName)
#if MyAppVersion == ""
#define MyAppVersion "1.0.0"
#endif

[Setup]
AppId={{B7E4A2D1-9F3C-4E86-A5B0-D8C1E2F4A697}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppNameNoSpace}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
; 相对路径的 OutputDir 是相对【脚本所在目录】(Inno 内部称 SourceDir)解析的,
; 所以这里写 "." 才会落在 Setup\ 下 —— 直接写 "Setup" 会变成 Setup\Setup\。
OutputDir=.
OutputBaseFilename={#MyAppNameNoSpace}-Setup-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
; 桌面快捷方式不强制勾选
ChangesAssociations=no
LicenseFile=..\LICENSE
UninstallDisplayIcon={app}\{#MyAppExeName}
; 图标已嵌入程序集,发布目录不再有 Assets\logo.ico,这里直接取仓库源码里的 ico
SetupIconFile=..\DSH Launcher\Assets\logo.ico
; 按用户安装(免管理员):装到 %LOCALAPPDATA%\Programs\DSHLauncher,与用户设置(%APPDATA%)一致
PrivilegesRequired=lowest
; 应用是常驻托盘的"单实例"程序,启动时持有命名 Mutex(见 App.axaml.cs: SingleInstanceMutexName)。
; 装/卸时若检测到该 Mutex,安装程序会提示用户先关闭应用 —— 否则用户数据文件被占用会删不干净。
; 名称必须与应用创建的字面量完全一致(Windows 下 Mutex 名区分大小写)。
AppMutex=DSH_Launcher_SingleInstance

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
chinesesimplified.InstallNeedNetRuntime=检测到缺少 .NET 10 Desktop Runtime,点击“确定”打开官网下载页面,安装运行时后重新运行本安装程序。
english.InstallNeedNetRuntime=.NET 10 Desktop Runtime is not installed. Click OK to open the download page, install the runtime, then run this installer again.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#MyPublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; NOTE: Don't use "Flags: ignoreversion" on any shared system files

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 卸载时连用户数据一起清掉(不保留任何残留)。
; 应用写入的两处根目录(见 PlatformProcess.RoamingAppDataDirectory / LocalAppDataDirectory):
;   %APPDATA%\DSH Launcher\Settings\settings.json          设置
;   %LOCALAPPDATA%\DSH Launcher\Settings\app.log           应用日志
;   %LOCALAPPDATA%\DSH Launcher\Settings\window-state.json 窗口位置/尺寸记忆
;   %LOCALAPPDATA%\DSH Launcher\WebView2\                  WebView2 用户数据目录(Cookie/缓存等,
;                                                          见 WebOpener.OnWebViewEnvironmentRequested)
Type: filesandordirs; Name: "{userappdata}\{#MyAppName}"
Type: filesandordirs; Name: "{localappdata}\{#MyAppName}"

[Code]
// .NET 安装器把 InstalledVersions 键写进【32 位】注册表视图(WOW6432Node),
// 而本安装器在 64 位模式下运行时 HKLM 指向 64 位视图 ⇒ 必须显式枚举两个视图。
const
  DesktopRuntimeKey = 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App';

// 枚举某注册表视图下的值名(每个已安装补丁是一个值,如 "10.0.12"),找 10.x。
// 注意:不能只判断子键是否存在 —— 装了 .NET 8 时子键同样存在。
function HasDesktopRuntimeInRegistry(RootKey: Integer): Boolean;
var
  Names: TArrayOfString;
  I: Integer;
begin
  Result := False;
  if RegGetValueNames(RootKey, DesktopRuntimeKey, Names) then
  begin
    for I := 0 to GetArrayLength(Names) - 1 do
    begin
      if Pos('10.', Names[I]) = 1 then
      begin
        Result := True;
        Exit;
      end;
    end;
  end;
end;

// 文件系统兜底:共享框架目录下是否存在 10.* 子目录(覆盖脚本安装到自定义目录的情况)
function HasDesktopRuntimeFolder(const Root: String): Boolean;
var
  FindRec: TFindRec;
begin
  Result := False;
  if FindFirst(Root + '\10.*', FindRec) then
  begin
    Result := (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0;
    FindClose(FindRec);
  end;
end;

// 命令兜底:dotnet --list-runtimes。
// 注意:重定向必须写在传给 cmd 的命令行里 —— ShellExec 的第 4 个参数是【工作目录】而不是输出文件,
// 把文件路径传进去不会产生任何输出,LoadStringFromFile 必然失败(旧实现的 bug)。
function HasDesktopRuntimeViaCli: Boolean;
var
  ResultCode: Integer;
  Output: AnsiString;
  TempFile: String;
begin
  Result := False;
  TempFile := ExpandConstant('{tmp}\dsh-runtimes.txt');
  if Exec(ExpandConstant('{cmd}'), '/C dotnet --list-runtimes > "' + TempFile + '" 2>&1',
          '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    if LoadStringFromFile(TempFile, Output) then
      Result := Pos('Microsoft.WindowsDesktop.App 10.', Output) > 0;
  end;
end;

// 检测 .NET 10 Desktop Runtime
function IsNet10DesktopRuntimeInstalled: Boolean;
begin
  Result :=
       HasDesktopRuntimeInRegistry(HKLM64)
    or HasDesktopRuntimeInRegistry(HKLM32)
    or HasDesktopRuntimeFolder(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App'))
    or HasDesktopRuntimeFolder(ExpandConstant('{localappdata}\Microsoft\dotnet\shared\Microsoft.WindowsDesktop.App'))
    or HasDesktopRuntimeViaCli;

  Log('检测 .NET 10 Desktop Runtime: ' + IntToStr(Ord(Result)));
end;

function InitializeSetup: Boolean;
var
  ErrorCode: Integer;
begin
  Result := True;
  if not IsNet10DesktopRuntimeInstalled then
  begin
    if MsgBox(CustomMessage('InstallNeedNetRuntime'), mbConfirmation, MB_YESNO) = IDYES then
      ShellExecAsOriginalUser('open', 'https://dotnet.microsoft.com/download/dotnet/10.0', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
    Result := False; // 中止安装,等用户装好运行时
  end;
end;
