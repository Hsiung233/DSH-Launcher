param(
    [Parameter(Mandatory = $true)][ValidateSet('已安装', '安装新插件')][string]$Tab,
    [int]$TimeoutSeconds = 15
)

# 让 DSH Launcher 插件页在左栏的「已安装 / 安装新插件」两个视图之间切换。
#
# 2026-09-16 起插件页不再是 FATabView 页签,而是**左栏 ListBox 视图切换**
# (列表密集的页面改成左右两栏:左边视图+选项,右边列表)。
# 因此这里找 `PluginsViewList` 里的 ListItem —— **只在左栏列表内查找**,
# 避免匹配到别处的同名文本(如「安装新插件」按钮)。
# **可以直接用 SelectionItemPattern.Select() 切换**;兜底用真实鼠标点击
# (先 SetWindowPos 置顶,SetForegroundWindow 常被系统限制)。

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @'
using System;
using System.Runtime.InteropServices;
public class TabClick {
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  public const uint DOWN = 0x0002, UP = 0x0004;
  public static readonly IntPtr TOPMOST = new IntPtr(-1);
  public static readonly IntPtr NOTOPMOST = new IntPtr(-2);
  public static void ToTop(IntPtr h) { SetWindowPos(h, TOPMOST, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0040); }
  public static void ToNormal(IntPtr h) { SetWindowPos(h, NOTOPMOST, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0040); }
  public static void Click(int x, int y) {
    SetCursorPos(x, y);
    System.Threading.Thread.Sleep(200);
    mouse_event(DOWN, 0, 0, 0, UIntPtr.Zero);
    System.Threading.Thread.Sleep(80);
    mouse_event(UP, 0, 0, 0, UIntPtr.Zero);
  }
}
'@

$ErrorActionPreference = 'Stop'

$root = [System.Windows.Automation.AutomationElement]::RootElement
$winCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::NameProperty, "DSH Launcher")
$viewListCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'PluginsViewList')
$itemCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::ListItem)
$textCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::Text)

function Get-AppWindow {
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $winCond)
}

function Get-ViewList($win) {
    return $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $viewListCond)
}

function Get-ItemName($item) {
    $text = $item.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $textCond)
    if ($text) { return $text.Current.Name }
    return $item.Current.Name
}

function Find-ViewItem($win, [string]$name) {
    $list = Get-ViewList $win
    if (-not $list) { return $null }
    foreach ($item in $list.FindAll([System.Windows.Automation.TreeScope]::Descendants, $itemCond)) {
        if ((Get-ItemName $item) -eq $name) { return $item }
    }
    return $null
}

function Get-SelectedView($win) {
    $list = Get-ViewList $win
    if (-not $list) { return '' }
    foreach ($item in $list.FindAll([System.Windows.Automation.TreeScope]::Descendants, $itemCond)) {
        $sel = $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
        if ($sel.Current.IsSelected) { return (Get-ItemName $item) }
    }
    return ''
}

$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
while ((Get-Date) -lt $deadline) {
    $win = Get-AppWindow
    if (-not $win) { Start-Sleep -Milliseconds 500; continue }

    $item = Find-ViewItem $win $Tab
    if (-not $item) { Start-Sleep -Milliseconds 700; continue }

    if ((Get-SelectedView $win) -eq $Tab) {
        Write-Host "已在 '$Tab' 视图"
        exit 0
    }

    try {
        $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        Start-Sleep -Milliseconds 900
        if ((Get-SelectedView (Get-AppWindow)) -eq $Tab) {
            Write-Host "已通过 SelectionItemPattern 切换到 '$Tab' 视图"
            exit 0
        }
    } catch {
        # 控件不支持该 Pattern 时忽略,改用真实点击
    }

    $handle = [IntPtr](Get-AppWindow).Current.NativeWindowHandle
    [TabClick]::ToTop($handle)
    Start-Sleep -Milliseconds 500
    $rect = (Find-ViewItem (Get-AppWindow) $Tab).Current.BoundingRectangle
    $clickX = [int]($rect.X + $rect.Width / 2)
    $clickY = [int]($rect.Y + $rect.Height / 2)
    [TabClick]::Click($clickX, $clickY)
    Start-Sleep -Milliseconds 1000
    [TabClick]::ToNormal($handle)
    Start-Sleep -Milliseconds 400

    if ((Get-SelectedView (Get-AppWindow)) -eq $Tab) {
        Write-Host "已通过鼠标点击切换到 '$Tab' 视图 于 ($clickX, $clickY)"
        exit 0
    }

    Write-Host "切换 '$Tab' 未生效,重试..."
    Start-Sleep -Milliseconds 600
}

Write-Host "超时:未能切换到 '$Tab' 视图(当前:$(Get-SelectedView (Get-AppWindow)))"
exit 1
