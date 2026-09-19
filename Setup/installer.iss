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
; 不写这条时,注册表卸载键的 DisplayName 取 AppVerName(=「AppName 版本 X」),
; Windows「设置→应用」里应用名就会带版本后缀。显式指定为纯应用名,与其他常见应用一致;
; 版本号仍显示在该列表的次要行(来自 DisplayVersion),信息不丢。
UninstallDisplayName={#MyAppName}
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
; 中文语言文件随仓库走(Setup\Languages\),不依赖 Inno Setup 安装目录里是否带它 ——
; CI(Runner 上 choco 装的 Inno Setup)就没有这个文件,用 compiler: 前缀会直接编译失败。
; 相对路径以 .iss 所在目录为基准。
Name: "chinesesimplified"; MessagesFile: "Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
chinesesimplified.InstallNeedNetRuntime=检测到缺少 .NET 10 Desktop Runtime,点击“确定”打开官网下载页面,安装运行时后重新运行本安装程序。
english.InstallNeedNetRuntime=.NET 10 Desktop Runtime is not installed. Click OK to open the download page, install the runtime, then run this installer again.
; 卸载时询问是否清理用户数据(弹框在 [Code] 的 CurUninstallStepChanged 里拼装,见那里的注释)。
; 注意:换行在代码里用 #13#10 拼,不要在这里写 %n —— MsgBox 不处理 %n。
chinesesimplified.UninstallUserDataAsk=是否同时删除用户数据?
english.UninstallUserDataAsk=Also delete your user data?
chinesesimplified.UninstallUserDataNote=「是」= 删除上面列出的目录(设置、日志、窗口位置记忆、WebView2 缓存),删除后无法恢复;「否」= 保留这些目录,以后重新安装时可继续使用原有设置。
english.UninstallUserDataNote=Yes = delete the folders listed above (settings, logs, window position, WebView2 cache) — this cannot be undone. No = keep them so a later reinstall reuses your current settings.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#MyPublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; NOTE: Don't use "Flags: ignoreversion" on any shared system files

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; ⚠ 不要改回默认写法(Setup 直接 CreateProcess 拉起应用):安装器把应用当子进程拉起时,安装器进程链上的
; **链级兼容层标记**会被继承到应用及其子进程,导致 dsh 解析不到 profile 依赖(整片 "Cannot find package '@deepseek-ai/*'")、
; 退出码 1 —— 这就是"安装完成后首次运行 dsh 必败、手动启动就好"的根因(2026-09-18 端到端实验闭环)。
; 改为经 explorer 启动:命令由**已在运行的 Explorer** 接管创建进程,父进程是 explorer、链根干净。
; 这与应用自身那条"进程链逃逸"用的是同一机制,已实测新实例 __COMPAT_LAYER 为空、EFC_* 为 0。
; 注意:这里**不要**换成 shellexec —— ShellExecuteEx 通常在 Setup 进程内创建子进程(父进程仍是 Setup),
; 断不了链;而应用侧另有一条**独立于本安装器**的通用自愈(见 App.axaml.cs 的 IsInsideInheritedCompatChain):
; 只要它发现自己带着链级兼容层标记,就自己经 explorer 逃逸重开。
; 所以本条修的是"正常路径不再需要自愈",而不是"可以删掉应用侧的自愈" —— 别的部署方式
; (企业推送、包装脚本、将来的自动更新器)照样会把应用放进自己的进程链。
; ⚠ 路径必须用 {win}(= C:\Windows\explorer.exe):explorer.exe **不在 System32 下**,写 {sys} 会在运行时找不到文件。
Filename: "{win}\explorer.exe"; Parameters: """{app}\{#MyAppExeName}"""; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 用户数据**不在这里删**:卸载时由 [Code] 的 CurUninstallStepChanged 弹框让用户选择是否清理
; (静默卸载用 /CLEANDATA 或 /KEEPDATA 指定,默认保留)。这里保留空段落只作说明。

[Code]
// .NET 安装器把 InstalledVersions 键写进【32 位】注册表视图(WOW6432Node),
// 而本安装器在 64 位模式下运行时 HKLM 指向 64 位视图 ⇒ 必须显式枚举两个视图。
const
  DesktopRuntimeKey = 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App';

  // 卸载时“是否清理用户数据”的命令行开关(静默卸载没法弹框,只能靠它表达):
  //   /CLEANDATA  清理(不弹框,直接删)
  //   /KEEPDATA   保留(不弹框,非静默时等价于在弹框里选“否”)
  SwitchCleanData = '/CLEANDATA';
  SwitchKeepData  = '/KEEPDATA';

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

// ---------------------------------------------------------------------------
// 卸载:用户数据是否清理由用户决定(代替原来的 [UninstallDelete] 无条件删除)
// ---------------------------------------------------------------------------
// 应用写入的用户数据(见 PlatformProcess.RoamingAppDataDirectory / LocalAppDataDirectory
// 与 WebOpener.OnWebViewEnvironmentRequested):
//   %APPDATA%\DSH Launcher\Settings\settings.json             设置
//   %LOCALAPPDATA%\DSH Launcher\Settings\app.log              应用日志
//   %LOCALAPPDATA%\DSH Launcher\Settings\window-state.json    窗口位置/尺寸记忆
//   %LOCALAPPDATA%\DSH Launcher\WebView2\                     WebView2 用户数据(Cookie/缓存)
// (注意:Pascal Script 里 var 段不能写在函数之后,所以不设全局变量,选择结果在下面的过程内用)

function UserDataDirRoaming: String;
begin
  Result := ExpandConstant('{userappdata}\{#MyAppName}');
end;

function UserDataDirLocal: String;
begin
  Result := ExpandConstant('{localappdata}\{#MyAppName}');
end;

// WebView2 的默认数据目录是 "<exe 所在目录>\<exe 名>.WebView2";正常情况下 WebOpener 会把它
// 改到 %LOCALAPPDATA%(见 OnWebViewEnvironmentRequested),只有那次改写失败时才留在程序目录里。
// 它位于程序目录内、且只是缓存(不是用户设置),所以**无条件**删掉 —— 否则卸载后 {app} 非空、
// 整个安装目录都会残留下来。
function WebView2FallbackDir: String;
begin
  Result := ExpandConstant('{app}\{#MyAppExeName}.WebView2');
end;

// 命令行里是否给了某个开关(/X 与 /X=1 两种写法都认)
function HasCommandLineSwitch(const Switch: String): Boolean;
var
  I: Integer;
  Arg: String;
begin
  Result := False;
  for I := 1 to ParamCount do
  begin
    Arg := Uppercase(ParamStr(I));
    if (Arg = Switch) or (Arg = Switch + '=1') then
    begin
      Result := True;
      Exit;
    end;
  end;
end;

// 删除一个用户数据目录(本来就不存在算成功),并把结果写进卸载日志
function DeleteUserDataDir(const Dir: String): Boolean;
begin
  if not DirExists(Dir) then
  begin
    Log('用户数据目录不存在,跳过: ' + Dir);
    Result := True;
    Exit;
  end;

  Result := DelTree(Dir, True, True, True);
  if Result then
    Log('已删除用户数据目录: ' + Dir)
  else
    Log('删除用户数据目录失败(可能被占用): ' + Dir);
end;

// 删除“开机自启动”注册项(用户在设置页开启过就会留下)。
// ⚠ 这一步**与“是否清理用户数据”无关,必须无条件执行**:程序文件都删了,注册项还留着的话,
// 每次登录 Windows 都会尝试启动一个不存在的 exe(用户看到的是报错,或干脆什么都没发生)。
// 值名与内容见 AutoStartService / AutoStartEntry(HKCU\...\Run 下名为 "DSH Launcher" 的字符串值);
// 这里**按名字删、不管它指向哪** —— 与设置页“设置是唯一真相”的口径一致。
procedure RemoveAutoStartEntry;
var
  Deleted: Boolean;
begin
  Deleted := RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'DSH Launcher');
  if Deleted then
    Log('已删除开机自启动项: HKCU\Software\Microsoft\Windows\CurrentVersion\Run\DSH Launcher')
  else
    Log('开机自启动项不存在(用户没开启过,或已被手动删除),无需处理');
end;

// 弹框询问是否清理用户数据:把两个具体目录列出来,默认按钮是「否」(保留),避免误删
function AskRemoveUserData: Boolean;
var
  Text: String;
begin
  Text :=
      CustomMessage('UninstallUserDataAsk') + #13#10 + #13#10
    + '    ' + UserDataDirRoaming + #13#10
    + '    ' + UserDataDirLocal + #13#10 + #13#10
    + CustomMessage('UninstallUserDataNote');

  Result := MsgBox(Text, mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
end;

// usUninstall = 已经过了 Inno 自带的「确定要卸载吗」与 AppMutex 的「应用正在运行,请先关闭」提示,
// 此时再问用户数据怎么处理,用户点取消卸载时就不会被问。
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  RemoveData: Boolean;
begin
  if CurUninstallStep <> usUninstall then
    Exit;

  DeleteUserDataDir(WebView2FallbackDir);

  // 自启动项**与“是否清理用户数据”无关**:程序删了它就必须走,见 RemoveAutoStartEntry 的说明
  RemoveAutoStartEntry;

  if HasCommandLineSwitch(SwitchCleanData) then
    RemoveData := True
  else if HasCommandLineSwitch(SwitchKeepData) then
    RemoveData := False
  else if UninstallSilent then
    // 静默卸载(/SILENT、/VERYSILENT)不能弹框:默认保留,要清理请显式加 /CLEANDATA
    RemoveData := False
  else
    RemoveData := AskRemoveUserData;

  if not RemoveData then
  begin
    Log('按用户选择保留用户数据: ' + UserDataDirRoaming + ' | ' + UserDataDirLocal);
    Exit;
  end;

  DeleteUserDataDir(UserDataDirRoaming);
  DeleteUserDataDir(UserDataDirLocal);
end;
