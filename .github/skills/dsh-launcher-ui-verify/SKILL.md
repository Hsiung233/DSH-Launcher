---
name: dsh-launcher-ui-verify
description: 'DSH Launcher 的界面端到端验证工作流:用 UI Automation 自主切换首页/设置页、按 AutomationId 读取或点击 Avalonia 控件、模拟真实键鼠输入、验收运行时界面行为。Use when: verifying or debugging this app UI at runtime; switching to the settings page without manual clicks; reading or clicking controls on the home or settings page; testing text-input filtering, focus-commit or range-clamping behavior; reproducing a UI regression in the DSH Launcher repo.'
user-invocable: true
---

# DSH Launcher 界面验证

本仓库是 Avalonia + FluentAvalonia + WebView2 的 Windows 常驻托盘应用。此 skill 用于**在运行时验证界面行为**——改了 AXAML 或 code-behind 之后确认实际效果，而不是只看代码。

## 何时使用

- 改了 `.axaml` 或界面相关的 code-behind，要确认运行时表现
- 需要读取或点击**首页**或**设置页**上的控件
- 需要模拟**真实键鼠输入**（验证输入过滤、失焦提交、范围夹取等）
- 复现或排查界面相关回归

## 前置信息

| 项 | 值 |
|---|---|
| 构建 | `dotnet build "DSH Launcher.slnx"` |
| 可执行文件 | `src\DSH Launcher/bin/Debug/net10.0/DSH Launcher.exe` |
| 设置 | `%APPDATA%\DSH Launcher\Settings\settings.json` |
| 日志 | `%LOCALAPPDATA%\DSH Launcher\Settings\app.log` |

要点：

- **GUI 常驻托盘**：用 `&` 或 `Start-Process` 启动（立即返回，不阻塞终端）；退出用 `Stop-Process` 或托盘菜单。
- **单实例**：互斥体 `DSH_Launcher_SingleInstance` + 两条命名管道。`DSH_Launcher_ShowMainWindow` 是"重复启动"信号,已有实例按**「重复启动应用时」设置**响应(配成 WebView/无动作时**不会**弹主界面);`DSH_Launcher_ForceShowWindow` 是**无条件显示主界面**(与设置无关,`switch-page.ps1` 唤起走它,或带 `--show-main-window` 启动)。
- **本机应用设置(2026-09-22 确认)**:`ShowMainWindowOnStartup=false` + `RunDshServiceOnStartup=true` + `RepeatLaunchAction=WebView` ⇒ 启动后**主界面不出现**,往往只有一个 WebView 窗口(`DSH Web`)。要操作界面必须先唤起主界面 —— ⚠ **别用"再启动一个实例"唤起**,那条路按设置响应(本机=开 WebView),主界面永远不出来(旧版脚本偶发成功是撞上服务未解析出地址的"回退打开主界面"分支)。
- **改设置后需重启应用**才生效（设置只在启动时加载；除 `WebOpener` 的超时是每次现读）。
- 构建前先 `Stop-Process -Name "DSH Launcher"`，否则 exe 被占用会报 `MSB3027`。
- ⚠⚠ **先看清你要结束的是哪一个实例**：如果**当前会话本身**就是启动器拉起来的（典型情形：经由启动器的 WebView 与
  编码助手对话，`dsh web` 是启动器的子进程），`Stop-Process -Name "DSH Launcher"` 会连同会话一起杀掉
  —— 本轮对话会被打断，得由用户手动重开。动手前先 `Get-Process -Name "DSH Launcher" | Select-Object Id, Path` 看
  `Path` 是不是你正要替换的那个产物；用户自己装着的那一份（`%LOCALAPPDATA%\Programs\DSHLauncher`）
  与仓库 `bin\` 下的调试产物是两回事，**构建不需要结束它**。

## 步骤

### 1. 启动并确认存活

```powershell
Stop-Process -Name "DSH Launcher" -Force -ErrorAction SilentlyContinue
dotnet build "DSH Launcher.slnx" 2>&1 | Select-Object -Last 5
& "src\DSH Launcher\bin\Debug\net10.0\DSH Launcher.exe"
Start-Sleep -Seconds 9
Get-Process -Name "DSH Launcher" | Select-Object Id, Responding, MainWindowTitle
```

### 2. 切到目标页

**必须用** [switch-page.ps1](./scripts/switch-page.ps1)——不要自己写选择逻辑，有几个反直觉的坑（见下节）：

```powershell
pwsh -NoProfile -File ".github\skills\dsh-launcher-ui-verify\scripts\switch-page.ps1" -Page "设置"
```

脚本会：唤起主界面（连 `DSH_Launcher_ForceShowWindow` 管道,不受「重复启动应用时」设置影响;管道连不上时兜底 `--show-main-window` 启动）→ 收掉 `DSH Web` 窗口（防遮挡/抢点击）→ 激活窗口 → 按导航项矩形中心真实点击 → 轮询等待页面加载。

### 3. 定位控件

用 `System.Windows.Automation`：

```powershell
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$root = [System.Windows.Automation.AutomationElement]::RootElement
$w = $root.FindFirst([System.Windows.Automation.TreeScope]::Children,
     (New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, "DSH Launcher"))
```

**按 `AutomationId` 查找**（值等于 XAML 里的 `x:Name`）：

```powershell
$c = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'WebViewIdleTimeoutBox')
$el = $w.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
```

> Avalonia 暴露的 `Name` 往往是**内容类型名**（如 `Avalonia.Controls.StackPanel`），按名字找不到控件；
> `ToolTip.Tip` 会映射到 `HelpText`，可作辅助判断。**主窗口刚启动时 UIA 树可能未就绪——要轮询重试。**

### 4. 读取与操作

| 需求 | 做法 |
|---|---|
| 读 Edit 文本 | `ValuePattern` → `.Current.Value` |
| 读/写 Spinner（`NumericUpDown`） | `RangeValuePattern`（`.Value/.Minimum/.Maximum/.SmallChange`）+ `SetValue` |
| 读 ToggleSwitch | 它是 `Button` 类型，读 `TogglePattern` 或看内部 `On`/`Off` 文本 |
| 点击按钮 | `InvokePattern.Invoke()` |
| **真实键盘输入** | `[System.Windows.Forms.SendKeys]::SendWait("...")`（**必须先 `SetFocus()` 且窗口在前台**） |
| 模拟关闭窗口 | `PostMessage(hwnd, WM_CLOSE, 0, 0)` |

> **`ValuePattern.SetValue` 会绕过 `TextInput`**，所以它**不能**用来验证输入过滤——那类测试必须用 `SendKeys`。

### 5. 断言

优先读**设置文件 / 日志**这类客观证据，而不是只看控件文本：

```powershell
Get-Content "$env:APPDATA\DSH Launcher\Settings\settings.json" | Select-String "ListenPort"
Get-Content "$env:LOCALAPPDATA\DSH Launcher\Settings\app.log" -Tail 8
```

## 控件 AutomationId 对照

**首页**（`HomePageControl`）

| AutomationId | 类型 | 出现条件 |
|---|---|---|
| `StatusText` | Text | 常驻（**判断"当前在首页"用它**）；在滚动区**外**，永远可见。文本是固定的产品名 `@deepseek-ai/dsh` |
| `StatusSubText` | Text | 常驻；版本号 `v0.1.5-rc.1` / `未安装` / `正在安装...` |
| `StatusBadgeText` | Text | 已安装且非安装中时才有；`运行中` / `已停止` |
| `CopyLogButton` / `ClearLogButton` / `OpenLogFileButton` | Button | 常驻（日志工具条） |
| `LogTextBlock` | Text | 常驻，其 `Name` **就是日志正文**（可用来判断"清空"是否生效） |
| `CopyEnvButton` / `EnvTextBlock` | Button / Text | 常驻（环境与详情），`EnvTextBlock.Name` = 多行环境信息正文 |
| `EnvExpander` | Group | 环境与详情所在的可折叠卡片，**默认折叠**（`ExpandCollapsePattern` 可展开） |
| `ContentScroll` | Pane | 环境/日志所在滚动区，`ScrollPattern` 可定位校验 |
| `InstallButton` | Button | 未安装 |
| `PortBox` | Spinner | 已安装且未运行 |
| `RunButton` | Button | 已安装且未运行 |
| `UpdateButton` | Button | 已安装、未运行、且有新版本 |
| `OpenInWebViewButton` / `OpenInBrowserButton` / `CopyWebUrlButton` | Button | 运行中且已检测到 Web 地址 |
| `RestartButton` / `StopButton` | Button | 运行中 |

> 首页 2026-09 起为 **WinUI 卡片式布局**：顶部一行是页头（`StatusText` + 状态徒标 + `StatusSubText` + 按钮组，在滚动区外），
> 下方 `ContentScroll`（`MaxHeight=340`）内是 **`EnvExpander`（默认折叠）**，最底部是**占满剩余高度的日志卡片**。
> `DshExpander`、`ExpanderHeader` **以及原先的 Web 地址卡片 `WebAddressPanel`/`WebAddressText` 均已不存在**。
>
> **Web 地址不再单独展示**：URL 只出现在日志正文里（`dsh web: http://...`），操作靠按钮组里的
> `CopyWebUrlButton`（文案 `复制URL`）+ `OpenInBrowserButton`/`OpenInWebViewButton`。
> 因此**验证地址的推荐做法：点 `CopyWebUrlButton` 后读剪贴板**（断言 `http*` 且含 `token=`），而不是去读某个控件文本。
>
> **`EnvExpander` 默认折叠**：读环境信息用 `EnvTextBlock.Current.Name`（折叠时也已有值，因为是在进页面时后台加载的，
> 不依赖 `Expanded` 事件）；但若要拿到它的**渲染尺寸**或点击内部内容，需先 `ExpandCollapsePattern.Expand()`。
> 日志卡片在根 Grid 的 `*` 行，会随内容区高度变化自动伸缩（折叠环境信息后日志明显变高）。
>
> 按钮的**显示文案已缩短**（与 AutomationId 无关，定位仍用 id）：
> `OpenInWebViewButton`=应用内打开、`OpenInBrowserButton`=浏览器打开、`CopyWebUrlButton`=复制URL（反馈时短暂变 `已复制`），
> 日志/环境两处复制按钮为 `复制`。**改名时必须同步代码后置里 `FlashCopiedAsync` 的还原文案**，
> 否则点一次后会永久停在 "已复制"。
> 判断服务状态建议用 `StatusBadgeText`（`运行中`/`已停止`），比解析 `StatusText` 可靠。
>
> **验证"控制条是否固定"**：先记录 `StatusText` 的 `BoundingRectangle.Y`，然后对 `ContentScroll`
> 调 `ScrollPattern.SetScrollPercent(NoScroll, 100)`，Y 不变即说明控制条在滚动区外。

> **`Grid` 等 Panel 没有自动化对等体**，即使设了 `x:Name` 也不会出现在 UIA 树里
> （如 `WebViewIdleTimeoutPanel` 在 UIA 树里就查不到）。判断这类容器是否可见，
> 要靠它的**子控件**能否找到。
>
> **展开 `Expander`（若界面重新引入）**：`ExpanderHeader` 是个 `Button` 但**不支持 `InvokePattern`**；
> 要在 `Expander`（`Group`）上使用 **`ExpandCollapsePattern`**（`Expand()`/`Collapse()`）。
> 另外 Avalonia 的 Expander **折叠时内容也在 UIA 树里**（只是不可见），所以按 AutomationId 能找到，
> 但依赖 `Expanded` 事件触发的逻辑不会执行。

**设置页**（`SettingsPageControl`）

| AutomationId | 类型 |
|---|---|
| `SettingsScroll` | Pane（**判断"当前在设置页"用它**） |
| `ShowMainWindowOnStartupSwitch` | Button（ToggleSwitch） |
| `RunDshServiceOnStartupSwitch` | Button（ToggleSwitch） |
| `AutoStartSwitch` | Button（ToggleSwitch，**开机自启动**；以 `dotnet` 主机启动时 `IsEnabled=false`） |
| `KeepWebViewAliveSwitch` | Button（ToggleSwitch） |
| `AfterDshServiceStartedCombo` | ComboBox |
| `TraySingleClickCombo` / `TrayDoubleClickCombo` | ComboBox |
| `WebViewLinkCombo` | ComboBox |
| `WebViewIdleTimeoutBox` | Spinner |
| `AutoStartStateText` | Text（开机自启动那一行下方：**系统侧真实状态**，`HelpText` = 自启动项位置与内容） |
| `NpmRegistryCombo` | ComboBox（**环境**卡片） |
| `NpmRegistryUrlText` | Text（**环境**卡片） |
| `ProxyUrlBox` / `NoProxyBox` | Edit（**环境**卡片） |

> **设置页的「开机自启动」行（2026-09-19 新增）**
>
> - 开关 `AutoStartSwitch` 回填的是**设置**（`settings.json` 的 `AutoStartOnLogon`，默认 false），
>   而它下面那行 `AutoStartStateText` 读的是**系统侧**（`AutoStartService.Read()`）—— 两者不一致时提示里会写明。
> - **验收必须看真实注册表，不能只看开关**：
>   ```powershell
>   reg query 'HKCU\Software\Microsoft\Windows\CurrentVersion\Run' /v 'DSH Launcher'
>   # 开启后应是 REG_SZ: "<exe 完整路径>" --autostart（路径含空格，带引号）
>   # 关闭后应是「找不到」；顺带确认 settings.json 里 AutoStartOnLogon 同步为 true/false
>   ```
> - 开→关→再开 一轮，确认 `app.log` 有 `[启动] 开机自启动 = 已开启(...)` / `= 已关闭,已清除系统自启动项`，
>   且**没有**重复的注册表写入（`Reconcile` 只在确有差异时才写）。
> - 以 `dotnet run` 启动时那一行是**禁用**的（拿不到程序本体路径），别把它当成 bug。
>
> **设置页的「环境」卡片（npm 源 / 代理，2026-09-17 新增）**
>
> - `NpmRegistryCombo` 5 个静态项，**顺序 = 枚举值**：`使用配置源`(0) / `npm 官方`(1) / `npmmirror(淘宝)`(2) /
>   `腾讯云`(3) / `华为云`(4)；写入 `settings.json` 的 `NpmRegistry`。
>   选“使用配置源”= 不覆盖本机 .npmrc，提示行会写 `当前:使用本机配置(.npmrc / 环境变量)`；
>   选镜像则写 `当前:<名称> <地址>`（读 `NpmRegistryUrlText.Current.Name` 断言）。
> - `ProxyUrlBox` **失焦或回车提交**，提交时会归一化：全角 `：／`→半角、缺协议补 `http://`、去尾斜杠。
>   ⇒ 用 `SendKeys` 输入 `127.0.0.1:1080` 时本机会打出**全角冒号**，读回的值应是 `http://127.0.0.1:1080`，
>   这正是验证归一化的现成手法（`SendKeys` 走真实键盘，比 `ValuePattern.SetValue` 更贴近用户）。
>   `ValuePattern.SetValue` 也能写，但**必须再把焦点移到别的控件**触发 `LostFocus` 才会提交。
> - `NoProxyBox` 是从属行（容器 `NoProxyPanel` 是 `Border`，**无 UIA 对等体**）：代理为空时整行禁用，
>   用 `NoProxyBox.Current.IsEnabled` 判断即可。
> - **枚举写入没有日志可看地址**？有：改这两项都会 `AppendSystemLog` 一行
>   `[环境] npm 源 = …` / `[环境] 代理 = …(不走代理: …)`，在 `app.log` 里可直接核对生效值。
>
> **验证“环境设置真的传给了子进程”**（实测过且无副作用）：
> ① registry：在首页安装/更新 dsh 时让包名指向一个不存在的包，npm/pnpm 的报错会带上实际请求地址；
>    选“使用配置源”时这里会变成你 .npmrc 里的源。
> ② 代理：把代理写成非法值（如 `127.0.0.1:9`）再跑同一条命令，报错里会出现代理连接失败。
> ③ 无副作用自查：失败安装不会改变已安装版本；改代理/registry 不影响已在运行的 `dsh web`。

**WebView 窗口**：标题 `DSH Web`，非本进程 UIA 树——按 PID 用 `EnumWindows` + `GetWindowThreadProcessId` 找窗口。
注意 `FindWindow(null, "DSH Web")` 在 PowerShell 里会被编组为空字符串导致匹配失败（句柄=0）。

## 关键坑（都踩过）

1. **必须先激活窗口**：`FANavigationView` 的导航项**只有窗口在前台时才以 `ListItem` 暴露**；否则只剩 `Text`，按 `ListItem` 查找一律失败 → 会误判成"设置页控件不存在"。
2. **`SelectionItemPattern.Select()` 无效**：返回 `True` 但页面不切换（导航项 `IsSelected` 也不变）。**必须真实鼠标点击**。
3. **每次点击前都要重新激活窗口**，否则后续点击落空。
4. 点击坐标取 **`ListItem`** 的 `BoundingRectangle` 中心；**同名的 `Text` 元素矩形是错的**（会点到窗口外）。
5. 判断当前页：设置页看 `SettingsScroll`，首页看 `StatusText`。**不能用 `PortBox`**——它在 `RunActionsPanel` 里，仅"已安装且未运行"时存在。
6. **UIA 矩形报 `∞` / 空 表示元素尺寸为 0**（不是没找到）。例如窗口过矮时日志区被压成 0，其 `BoundingRectangle` 就会是 `∞`；
   这可用于快速判断"元素被挤没了"。
7. 主界面可能隐藏在托盘：用 `switch-page.ps1` 的 ForceShow 管道唤起；**不要**靠再启动一个实例（见前置信息）。
8. **不要用 `ValuePattern` 验证输入过滤**，它会绕过 `TextInput`；用 `SendKeys`。
9. **不要在终端里用 `Assembly.LoadFrom` 加载 `bin` 下的程序集**（含 `DSH Launcher.dll`）——会把输出 DLL 锁住，
   后续 `dotnet build` 报 `MSB3027/MSB3021 文件被占用`。已加载过后需结束那个 PowerShell 进程（可能就是你当前终端）。
   如需查看程序集 API，改用 `dotnet msbuild -getItem:` 或在**子进程**里跑一次性脚本。
10. **改动后务必用 `Select-String` 核对磁盘**：曾出现过编辑报告成功、`read_file` 也能读到，
    但磁盘上实际未生效/被回退，导致后续一堆莫名其妙的编译错误。
11. **截图看效果**比只读 UIA 树可靠得多：同色背景、大片空白、控件挤压这类问题**只能靠看图发现**。
    `System.Drawing` + `CopyFromScreen`（配合 `TransformPattern`/`MoveWindow` 定尺寸、按 `VirtualScreen` 裁剪）即可；
    注意 Mica 是半透明的，窗口边缘会透出桌面，属正常现象。
12. PowerShell 脚本里**别用 `R`、`r` 这类单字母函数名**——与 `Invoke-History` 别名冲突（`r`）会报"找不到接受自变量的位置参数"。
13. **截图前先把窗口置顶**（`SetWindowPos(h, HWND_TOPMOST, ...)`，截完再 `HWND_NOTOPMOST`）：
    `SetForegroundWindow` 常被系统限制而失效，截出来的会是挡在前面的其他窗口（比如浏览器）。
14. **改完 AXAML 若报“找不到某个事件处理器”，先构建一次再判断**：AXAML 分析器的报错可能是上一版的陈旧结果
    （code-behind 里明明有对应方法，`get_errors` 仍报缺失）。
15. **`SetWindowPos` 置顶时必须带 `SWP_NOMOVE(0x0002)`**（`SWP_NOSIZE|SWP_NOMOVE|SWP_SHOWWINDOW` = `0x0043`）。
    只写 `SWP_NOSIZE` 会把窗口**移动到 (0,0)** —— 而应用会把窗口位置写进 `window-state.json`，
    等于改坏用户的窗口布局（本次就踩到了，窗口被压到最小尺寸停在左上角）。截完图记得把窗口恢复原样。
16. **`Border` / `Grid` / `ItemsControl` / `Ellipse` 都没有 automation peer**：即使设了 `x:Name` 也不会出现在 UIA 树里。
    需要判断这类元素的存在/显隐时，用它的**子元素**当锚点，而不是容器本身。
17. **用 `MoveWindow` 改窗口尺寸后，尺寸会在后续 Show/布局时漂移**（实测 1680x945 → 1733x993 → 2207x1034），
    所以“缩到最小窗口看效果”这类实验**必须显式恢复一次原尺寸**再结束。
    好消息：`%APPDATA%\DSH Launcher\Settings\window-state.json` **只在隐藏/退出时才写**，
    所以这些截图实验不会污染用户持久化的窗口几何。
18. **截图前要把窗口真正抬到最前**：只 `SetWindowPos(TOPMOST)` 有时不够 —— 实测截到过浏览器/VSCode 的像素
    （窗口其实在后面）。先 `ShowWindow(SW_RESTORE)` + `SetForegroundWindow` 再置顶，**拷贝前重新读一次**
    `BoundingRectangle`，并顺便打印 `GetForegroundWindow()` 是否就是该窗口来自查。
19. **Fluent 的滚动条是 overlay（画在内容之上，不占布局）**：不给内容留右槽位就会盖住控件右边缘。
    修法：给滚动内容加右边距，并且**把容器宽度补回来**，保证控件净宽不缩水。
    自查方法：截图后用 `GetPixel` 沿一行扫出与底色不同的列区间，看控件右边界与滚动条位置是不是分开了。
20. ⚠ **Avalonia 的 `ComboBox` 会在构造时自动选中第 0 项**（即使 XAML 没写 `SelectedIndex`），
    那个 `SelectionChanged` 发生在 `InitializeComponent` 期间、**早于** `AttachedToVisualTree`。
    如果处理程序里会写设置，就会把用户的选择静默改成第 0 项。
    修法：加一个 `_pageReady` 标志，在进页面、回填完毕后才置位；处理程序开头 `if (!_pageReady) return;`
    （光靠 `_initializing` 不够 —— 它要到进页面时才置位）。
    验证手法：把设置改成**非第 0 项**的值 → 启动 → 只进一次页面 → 看 `settings.json` 有没有变。
21. ⚠ **不要靠肉眼判断“日志里是不是乱码”**：终端会把 **U+2009（窄空格）** 与 **U+2715（✕）** 渲染成 `?`，
    看起来像解码失败，其实是终端字体缺字。
    正确做法：**按码点断言** —— 读 `LogTextBlock` 的 `Name`，检查 `Contains([char]0x2009)` / `Contains([char]0x2715)` 等，
    再用 `[regex]::Match($log, '.{0,2}WARN.{0,2}').Value` 把码点列出来看。
    拿不准就去抓**原始字节**（子进程输出按 Latin1 收，打印十六进制），比猜字符靠谱。
22. **子进程输出编码：UTF-8 与系统代码页会混在同一条流里**（dsh/pnpm 日志乱码踩过）：
    - `ProcessStartInfo` 开了重定向但**没设 `StandardOutputEncoding`** 时，.NET 回退到系统 ANSI 代码页
      （中文 Windows = GBK/936）。
    - 而 **Node 系工具（dsh / npm / pnpm）写 UTF-8** ⇒ 必然乱码：`…WARN…` → `鈥塛ARN鈥?`、`└─┬` → `鈹溾攢鈹?`、`✕` → `鉁?`。
    - **但不能一刀切换成 UTF-8**：`cmd.exe` 自己的消息（如 `'xxx' 不是内部或外部命令`）是 **OEM 代码页**，
      且两种输出会落在**同一管道（甚至同一 stderr）**上。实测 `chcp 65001` 对**重定向的 cmd** 输出**无效**。
    - 解法：重定向编码用 **`Encoding.Latin1`**（字节↔字符无损，当“原样字节”通道），
      拿到字符串后再逐行判定：**先试严格 UTF-8**（`new UTF8Encoding(false, throwOnInvalidBytes: true)`），
      抛 `DecoderFallbackException` 就回退 OEM 代码页。GBK 第二字节常落在 0x40-0x7F，不是合法 UTF-8 续字节，所以判定很可靠。
    - 验证：让首页日志里出现带盒线字符与窄空格的输出（如安装/更新 dsh 的 pnpm 警告树），
      检查 `LogTextBlock` 里对应码点正确。
23. **`CheckBox` 不支持 `InvokePattern`，只能用 `TogglePattern`**：
    对着 CheckBox 取 `InvokePattern` 会抛「不支持的模式」。另外**部分元素取 Pattern 也会失败**
    （离屏/未实现的虚拟化行），遍历时要逐个 `try/catch` 跳过，不要假设全都能交互。
24. **PowerShell 里全角引号 `“”` 也是字符串定界符**：在双引号字符串里写 `“批量禁用”` 会导致解析报
    「意外的标记」。中文断言标签请改用 `「」`。

## 验证完记得收尾

```powershell
Stop-Process -Name "DSH Launcher" -Force
# 恢复被测试改动的设置,并重启应用
# 删除本次创建的一次性临时脚本
```

页面切换脚本是**常驻工具**（本 skill 的 `scripts/`），不要当作一次性脚本删掉。
