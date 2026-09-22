param(
    [Parameter(Mandatory = $true)][ValidateSet('首页', '插件', '设置')][string]$Page,
    [int]$TimeoutSeconds = 20
)

# 让 DSH Launcher 主窗口自主切换到指定页(首页/插件/设置),供自动化界面验证使用。
#
# 踩过的坑(务必保留这些做法,详见 ../SKILL.md):
# 1) **必须先激活窗口**:FANavigationView 的导航项只有在窗口处于前台时,
#    才会在 UIA 树里以 ListItem 暴露;否则只剩 Text,按 ListItem 查找一律失败,
#    会误判成“设置页控件不存在”。
# 2) **SelectionItemPattern.Select() 无效**:它返回 true,但页面不会切换
#    (实测导航项的 IsSelected 也不变)。必须用真实鼠标点击。
# 3) **每次点击前都要重新激活**:上一次点击/切页会改变前台状态,不重新激活则后续点击落空。
# 4) 点击坐标取 ListItem 的 BoundingRectangle 中心。不要用同名的 Text 元素,
#    实测它的矩形是错的(会点到窗口外)。
# 5) 切页后控件是异步加载的,要轮询等待,不能固定 sleep。
# 6) **主界面默认不出现**(应用设置 ShowMainWindowOnStartup=false,启动后可能只有一个 WebView 窗口 "DSH Web"):
#    必须先唤起主界面。⚠ 不能靠"再启动一个实例"唤起 —— 已有实例按**"重复启动应用时"设置**响应,
#    那项配成 WebView/无动作时(本机就是 WebView)主界面根本不出来。正解是连专用"显示主界面"命名管道
#    (SingleInstanceGuard.ForceShowPipeName,收到连接就无条件 ShowMainWindow,与设置无关)。
# 7) **"DSH Web" 窗口会盖在主窗口上抢走真实鼠标点击**,点导航前先把它收掉
#    (发 WM_CLOSE,应用对关闭的处理是 Hide,WebView 会话保留、之后照常复用)。

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

Add-Type @'
using System;
using System.Runtime.InteropServices;
public class NavClick {
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
  public delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
  [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int m);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  public const uint DOWN = 0x0002, UP = 0x0004, WM_CLOSE = 0x0010;
  public static void Click(int x, int y) {
    SetCursorPos(x, y);
    System.Threading.Thread.Sleep(150);
    mouse_event(DOWN, 0, 0, 0, UIntPtr.Zero);
    System.Threading.Thread.Sleep(80);
    mouse_event(UP, 0, 0, 0, UIntPtr.Zero);
  }

  // 关掉指定进程名下、指定标题的顶层窗口(用于收掉 "DSH Web")。
  // 用 C# 内部回调而不是 PowerShell 脚本块转委托,免得踩编组的坑。
  public static int CloseWindowsByTitle(uint pid, string title) {
    var closed = 0;
    EnumWindows((h, l) => {
      uint p;
      GetWindowThreadProcessId(h, out p);
      if (p != pid) { return true; }
      var sb = new System.Text.StringBuilder(256);
      GetWindowText(h, sb, 256);
      if (sb.ToString() == title) {
        PostMessage(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        closed++;
      }
      return true;
    }, IntPtr.Zero);
    return closed;
  }
}
'@

$ErrorActionPreference = 'Stop'

# 从脚本位置向上找到仓库根(含 DSH Launcher.slnx 的目录)
$repoRoot = $PSScriptRoot
while ($repoRoot -and -not (Test-Path (Join-Path $repoRoot 'DSH Launcher.slnx'))) {
    $parent = Split-Path -Parent $repoRoot
    if (-not $parent -or $parent -eq $repoRoot) { break }
    $repoRoot = $parent
}

function Get-AppWindow {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $c = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, "DSH Launcher")
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $c)
}

function Activate-AppWindow($win) {
    $h = [IntPtr]$win.Current.NativeWindowHandle
    [void][NavClick]::ShowWindow($h, 9)          # SW_RESTORE
    [void][NavClick]::SetForegroundWindow($h)
    Start-Sleep -Milliseconds 700
}

# 收掉 "DSH Web"(WebView 窗口):它与主窗口同进程,盖在上面会抢走真实鼠标点击。
# 应用对关闭的处理是 Hide(会话保留),之后复用照常,不影响验证。
function Hide-DshWebWindow($win) {
    $h = [IntPtr]$win.Current.NativeWindowHandle
    $procId = [uint32]0
    [void][NavClick]::GetWindowThreadProcessId($h, [ref]$procId)
    $closed = [NavClick]::CloseWindowsByTitle($procId, 'DSH Web')
    if ($closed -gt 0) {
        Write-Host "已收掉 $closed 个 DSH Web 窗口(避免遮挡/抢点击)"
        Start-Sleep -Milliseconds 500
    }
}

function Get-NavItem($win, [string]$name) {
    $liCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::ListItem)
    foreach ($el in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $liCond)) {
        if ($el.Current.Name -eq $name) { return $el }
    }
    return $null
}

function Get-CurrentPage($win) {
    $c1 = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'SettingsScroll')
    if ($win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c1)) { return '设置' }
    # 插件页用 PluginsSubText(页头常驻文本;Grid/ItemsControl 这类 Panel 没有 UIA 对等体,查不到)
    $cPlugins = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'PluginsSubText')
    if ($win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cPlugins)) { return '插件' }
    # 首页用 StatusText(常驻文本)。不要用 PortBox —— 它在 RunActionsPanel 里,
    # 只有“已安装且未运行”时才存在,服务运行中查不到。
    # (原先用 DshExpander;2026-09 首页去掉 Expander 后改为 StatusText)
    $c2 = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'StatusText')
    if ($win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c2)) { return '首页' }
    return '未知'
}

$win = Get-AppWindow
if (-not $win) {
    # 主界面默认不显示(ShowMainWindowOnStartup=false),先唤起:
    # 连**专用"显示主界面"命名管道**(SingleInstanceGuard.ForceShowPipeName)——
    # 已有实例收到连接就无条件 ShowMainWindow。⚠ 不要退回"再启动一个实例"的普通启动:
    # 那条路按"重复启动应用时"设置响应,配成 WebView/无动作时主界面永远不会出来。
    Write-Host '主界面未出现,唤起中...'
    for ($i = 0; $i -lt 10 -and -not $win; $i++) {
        try {
            $pipe = New-Object System.IO.Pipes.NamedPipeClientStream(
                '.', 'DSH_Launcher_ForceShowWindow', [System.IO.Pipes.PipeDirection]::Out)
            $pipe.Connect(1000)
            $pipe.Dispose()
        }
        catch {
            # 连接失败:应用没在跑,或监听方正处在"上一连接刚结束、重建中"的间隙,稍后重试
        }
        for ($j = 0; $j -lt 10 -and -not $win; $j++) {
            Start-Sleep -Milliseconds 300
            $win = Get-AppWindow
        }
    }
}
if (-not $win) {
    # 兜底:应用可能根本没起来。带 --show-main-window 启动:
    # 已有实例会收到管道通知并显示主界面(本次进程随即退出);没有实例时它自己就是主实例、直接显示主界面
    $exe = Join-Path $repoRoot 'DSH Launcher\bin\Debug\net10.0\DSH Launcher.exe'
    if (Test-Path $exe) {
        Write-Host '管道未连上,改用 --show-main-window 启动兜底...'
        Start-Process -FilePath $exe -ArgumentList '--show-main-window' | Out-Null
        for ($i = 0; $i -lt 20 -and -not $win; $i++) {
            Start-Sleep -Milliseconds 500
            $win = Get-AppWindow
        }
    }
}
if (-not $win) { Write-Host '无法获取主窗口(应用没在跑?或不是带 ForceShow 管道的新版)'; exit 1 }

Hide-DshWebWindow $win

Activate-AppWindow $win

if ((Get-CurrentPage (Get-AppWindow)) -eq $Page) {
    Write-Host "已在 '$Page' 页"
    exit 0
}

$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
while ((Get-Date) -lt $deadline) {
    # 每次点击前重新激活并重取矩形(前台状态与矩形都可能变化)
    $win = Get-AppWindow
    Activate-AppWindow $win

    $win = Get-AppWindow
    $item = Get-NavItem $win $Page
    if ($item) {
        $r = $item.Current.BoundingRectangle
        $x = [int]($r.X + $r.Width / 2)
        $y = [int]($r.Y + $r.Height / 2)
        [NavClick]::Click($x, $y)
        Write-Host "点击导航项 '$Page' 于 ($x, $y)"
    }

    for ($j = 0; $j -lt 8; $j++) {
        Start-Sleep -Milliseconds 400
        if ((Get-CurrentPage (Get-AppWindow)) -eq $Page) {
            Write-Host "已切换到 '$Page' 页"
            exit 0
        }
    }
}

Write-Host "超时:未能切换到 '$Page' 页"
exit 1
