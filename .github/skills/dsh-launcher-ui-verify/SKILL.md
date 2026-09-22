---
name: dsh-launcher-ui-verify
description: 'DSH Launcher 的界面端到端验证工作流:用 UI Automation 自主切换首页/插件页/设置页、按 AutomationId 读取或点击 Avalonia 控件、模拟真实键鼠输入、验收运行时界面行为。Use when: verifying or debugging this app UI at runtime; switching to the settings or plugins page without manual clicks; reading or clicking controls on the home, settings or plugins page; testing text-input filtering, focus-commit or range-clamping behavior; reproducing a UI regression in the DSH Launcher repo.'
user-invocable: true
---

# DSH Launcher 界面验证

本仓库是 Avalonia + FluentAvalonia + WebView2 的 Windows 常驻托盘应用。此 skill 用于**在运行时验证界面行为**——改了 AXAML 或 code-behind 之后确认实际效果，而不是只看代码。

## 何时使用

- 改了 `.axaml` 或界面相关的 code-behind，要确认运行时表现
- 需要读取或点击**设置页**、首页、**插件页**上的控件
- 需要在插件页的**页签**（已安装 / 安装新插件）之间切换
- 需要模拟**真实键鼠输入**（验证输入过滤、失焦提交、范围夹取等）
- 复现或排查界面相关回归

## 前置信息

| 项 | 值 |
|---|---|
| 构建 | `dotnet build "DSH Launcher.slnx"` |
| 可执行文件 | `DSH Launcher/bin/Debug/net10.0/DSH Launcher.exe` |
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
& "DSH Launcher\bin\Debug\net10.0\DSH Launcher.exe"
Start-Sleep -Seconds 9
Get-Process -Name "DSH Launcher" | Select-Object Id, Responding, MainWindowTitle
```

### 2. 切到目标页

**必须用** [switch-page.ps1](./scripts/switch-page.ps1)——不要自己写选择逻辑，有几个反直觉的坑（见下节）：

```powershell
pwsh -NoProfile -File ".github\skills\dsh-launcher-ui-verify\scripts\switch-page.ps1" -Page "设置"
```

脚本会：唤起主界面（连 `DSH_Launcher_ForceShowWindow` 管道,不受「重复启动应用时」设置影响;管道连不上时兜底 `--show-main-window` 启动）→ 收掉 `DSH Web` 窗口（防遮挡/抢点击）→ 激活窗口 → 按导航项矩形中心真实点击 → 轮询等待页面加载。

**插件页内部的页签**用 [switch-tab.ps1](./scripts/switch-tab.ps1)（不需要真实点击，`SelectionItemPattern.Select()` 就有效）：

```powershell
pwsh -NoProfile -File ".github\skills\dsh-launcher-ui-verify\scripts\switch-tab.ps1" -Tab "安装新插件"
```

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
> **`EnvExpander` 默认折叠**：读环境信息用 `EnvTextBlock.Current.Name`（拄叠时也已有值，因为是在进页面时后台加载的，
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

**插件页**（`PluginsPageControl`,2026-09 新增,导航第二项,标签 `plugins`）

页面结构:`页头(插件 + pnpm 徽标 + profile 说明 + 插件目录/刷新/输出)` / `左右两栏(左=视图+选项,右=列表)` / `日志浮窗(从页头下方垂下)`。
**底部已无常驻的「操作输出」卡片了**（2026-09 改成浮窗）；「输出」按钮 **2026-09-16 从右下角浮动按钮挪到了页头**
（刷新之后）,所以内容区不再需要 48 的底部避让边距（已改成 24）。

**左右两栏（2026-09-16 起，不再是 FATabView 页签）**：
- **左栏 `PluginsViewList`**（`ListBox`，宽 212）：顶部是视图切换（`已安装` / `安装新插件`），下面是当前视图用得上的选项；
  **「手动安装」固定在栏底**（它在 `*,Auto` 网格的第 2 行，不随上面的选项一起滚），其余选项在栏内可滚动。
- **右栏**是两个面板二选一：`InstalledView`（两列：`已安装的插件` | `全部组合条目`）/
  `InstallView`（搜索行 + 目录列表）。**隐藏的那一个 `IsVisible=false`，其中的控件不在 UIA 树里**。
- 两列列表各自撑满、在卡片**内部**滚动（外层不放 `ScrollViewer`，否则列被无限高度测量、撑不满）。
- 这么改的原因：最小窗口（630 高）下原来两行筛选吃掉 ~110px，列表只剩两三行；
  改完安装视图从 2 行变 4-5 行，组合条目从 6 行变 8 行。

**「安装新插件」视图的来源是策展目录**（两份社区目录都支持,自动识别结构），**不是 npm registry**：
- `awesome-dsh-plugin`（默认）—— 社区市场 **dsh-market** 的数据源，`awesome-dsh-plugin.com/plugins.json`，
  顶层是对象 + `plugins` 数组，字段完整单词，**3722 条**，有 `updated` 与下载量。
- `dsh-plugin.org` —— 插件中心 **dsh-plugin-hub** 的数据源，`api.dsh-plugin.org/plugins.zh.json`，
  顶层**就是数组**、字段是缩写（`n/o/c/d/ic/igc/v/sg/fk/vr`），**9356 条**（全部 `verified`），
  **没有 updated 字段、没有下载量**（所以状态行会只显示“共 N 个插件”，行上也没有“下载”）。

搜索/分类/排序都是**在本地已拉取的目录里过滤**（不发起 npm 搜索）;安装用的是目录每条自带的命令
（awesome-dsh-plugin 的 `install` / dsh-plugin.org 的 `ic`），装不上时回退到备选来源
（`tarball` / `igc`），即上游的「npm → 预构建 tarball → 整仓源码」顺序。
目录 3-7MB，**切到该视图时才拉一次**（2026-09-16 起**按地址缓存在服务层**，来回切来源不重复下载）；
「刷新目录」才强制重拉（`forceReload: true`，会绕过并覆盖缓存）。

> **验证目录缓存/竞态的关键手法**：两份目录条数不同（**3722** vs **9356**），
> 而 `CatalogStatusText` 会显示“共 N 个插件” ⇒ **用条数就能精确断言当前列表属于哪份目录**，
> 不必去数列表行（列表已虚拟化，数不准）。
> 更硬的自查：数 `app.log` 里本次运行的“已读取插件目录”行数（每次真正下载才写一行）。
> 拉十几下来源应当**每个地址只出现一次**。

**切视图用 [switch-tab.ps1](./scripts/switch-tab.ps1)** —— 它**只在 `PluginsViewList` 里找 `ListItem`**（避免碰到别处的同名文本），
**可以直接用 `SelectionItemPattern.Select()` 切换**（与顶部 `FANavigationView` 不同,那里 Select() 无效）。

| AutomationId | 类型 | 所在视图 | 说明 |
|---|---|---|---|
| `PluginsViewList` | List（ListBox） | 左栏 | 视图切换；两项分别是 `已安装` / `安装新插件`，**判断当前视图看哪个 `ListItem` 的 `IsSelected`** |
| `PluginsSubText` | Text | 页头 | **判断“当前在插件页”用它**（`Grid`/`ItemsControl` 这类 Panel 没有 UIA 对等体，查不到） |
| `PnpmBadge` / `PnpmBadgeText` | Text | 页头 | pnpm 不可用时显示“pnpm 未就绪” |
| `RevealProfileButton` / `RefreshPluginsButton` | Button | 页头 | 插件目录 / 刷新 |
| `InstalledView` | Grid（**无 UIA 对等体**） | 已安装 | 单列表布局容器（`Auto,*` 行:批量栏 + 「插件与条目」卡片）;判视图请用 `PluginsViewList` 的选中项 |
| `SelectAllBox` | CheckBox（`Name=全选`） | 左栏（仅已安装视图，「批量操作」组） | 全选/取消全选**当前可见**的条目（受筛选影响）。**CheckBox 只支持 `TogglePattern`，不支持 `InvokePattern`**。2026-09-22 起批量控件从列表上方的批量栏挪进左栏 |
| `BatchCountText` | Text | 左栏（仅已安装视图） | `未选择任何条目` 或 `已选 N 项 · 可启停 A · 可恢复 B · 可卸载 C` —— **拿到 A/B/C 就能断言按钮该不该可用**（窄栏里会换行） |
| `BatchEnableButton` / `BatchDisableButton` | Button | 左栏（仅已安装视图，2×2 网格上排） | 批量写 `disabled: false/true`;选中项里没有可启停的时 **IsEnabled=false** |
| `BatchResetButton` | Button | 左栏（仅已安装视图，2×2 网格下排左） | 删除启停覆盖;没有 `HasOverride` 的选中项时禁用 |
| `BatchUninstallButton` | Button | 左栏（仅已安装视图，2×2 网格下排右） | 批量卸载（弹 `FAContentDialog` 确认）;没有可卸载项时禁用 |
| 行内选择框 | CheckBox（**无 AutomationId**） | 列表行 | `IsChecked` 双向绑定在 `PluginEntry.IsSelected` 上。取行内选择框要用 `ControlType=CheckBox` 且**排除 `Name=全选`** |
| `InstalledEmptyPanel` / `GoToInstallTabButton` | Panel / Button | 已安装 | 没装任何插件时的空态提示 /「去安装新插件」按钮(点了切到第 2 个视图) |
| `AllEntriesCard` | Border（**无 UIA 对等体**） | 已安装 | 「插件与条目」卡片(单列表;原先的「已安装」「全部组合条目」两列已合并) |
| `AllEntriesScroll` | Pane | 已安装 | 单列表滚动区（可当“在已安装视图”的锚点） |
| `AllEntriesCountText` | Text | 已安装 | 计数文本:`已安装 N 个插件 · 共 M 条`,或(被默认收起/筛选时)`已安装 N 个插件 · 显示 X 条 / 共 M 条`(实测共 153) |
| `ShowAllEntriesBox` | CheckBox(`Name=显示全部`) | 已安装·卡片头 | 「显示全部」开关,**默认 Off** = 列表只显示已安装插件的条目与有启停覆盖的条目(自带条目默认收起);勾选后显示全部。**只支持 `TogglePattern`**。筛选框有内容时始终搜全部,不受它影响 |
| `EntryFilterBox` | Edit | 左栏（仅已安装视图） | 按包名/条目 id 筛选；**要操作某一行必须先靠它缩窄列表**(列表已虚拟化,屏幕外的行不在 UIA 树里)。**已从卡片标题栏挪到左栏** |
| `AllEntriesList` | ItemsControl（**无 UIA 对等体**） | 已安装 | 单列表(操作面 + 诊断),已虚拟化:任何时刻 UIA 里只有可视区域的那几行（实测 8 行/153 条） |
| `InstallView` | Grid（**无 UIA 对等体**） | 安装新插件 | 搜索行 + 目录列表卡的容器 |
| `CatalogSearchBox` | Edit | 安装新插件 | 搜索目录(包名/作者/描述),**改文本即过滤**(不用回车) |
| `CatalogCategoryCombo` | ComboBox | 左栏（仅安装视图） | 分类过滤;项是运行时按目录填的(第一项=`全部分类`),**用 `SelectionItemPattern.Select()` 选** |
| `CatalogSortCombo` | ComboBox | 左栏（仅安装视图） | 排序(按星标/下载量/收录时间),静态 3 项 |
| `CatalogSourceCombo` | ComboBox | 左栏（仅安装视图） | 目录来源，**2 个静态项且顺序 = 枚举值**：`awesome-dsh-plugin`(0) / `dsh-plugin.org`(1)；写入 `settings.json` 的 `PluginCatalog`。**换来源会强制重拉** |
| `RefreshCatalogButton` | Button | 左栏（仅安装视图） | 重新拉目录(3-7MB,实测 0.1-5.4s) |
| `CatalogStatusText` | Text | 安装新插件 | **计数/状态行**:`共 N 个插件[ · 目录更新于 YYYY-MM-DD]` 或 `命中 N 个 / 全部 M 个`,失败时是错误原因；来源地址在它的 **HelpText(tooltip)** 里。**dsh-plugin.org 没有 updated 字段**,所以那条来源下不会有“目录更新于” |
| `CatalogListScroll` | Pane | 安装新插件 | 目录结果滚动区(可当“在安装视图”的锚点) |
| `CatalogList` | ItemsControl（**无 UIA 对等体**） | 安装新插件 | 目录结果列表;行上按钮文案 `安装`、`详情` |
| `InstallSpecBox` / `InstallPluginButton` | Edit / Button | 左栏（仅安装视图） | **手动安装**(目录外的包:本地路径/私有仓库)。**固定在左栏底部**,不随选项一起滚 |
| `PluginLogToggleButton` | Button | **页头**（刷新之后） | 开/关日志浮窗；浮窗关着时来了新输出会在它内部亮一个小圆点 |
| `PluginLogFlyout` | Border（**无 UIA 对等体**） | — | 日志浮窗本体,`Grid.Row="1"` + 右上对齐 + `VerticalAlignment="Top"`（从页头下方垂下,盖在列表上）。**用 `PluginLogScroll` 是否存在来判断浮窗开没开** |
| `PluginLogDismissLayer` | Border（**无 UIA 对等体**） | — | 「点空白处关闭」的透明命中层,盖住内容区(第 1 行)。**无 UIA 对等体、也没有 Invoke 模式 ⇒ 只能用真实鼠标点击验证**（`SetCursorPos` + `mouse_event`） |
| `PluginLogScroll` / `PluginLogTextBlock` | Pane / Text | — | 浮窗内的日志区；`PluginLogTextBlock.Name` 就是全部日志文本 |
| `CopyPluginLogButton` / `CopyPluginLogButtonText` | Button / Text | — | 复制（文案短暂变 `已复制`）；**`CopyPluginLogButtonText` 是判断文案的锚点** |
| `ClearPluginLogButton` | Button | — | 清空日志 |
| `ClosePluginLogButton` | Button | — | 关掉浮窗 |
| `PluginLogUnreadDot` | Ellipse（**无 UIA 对等体**） | — | 浮窗关着时来了新日志就亮的小圆点；**只能靠截图验证** |

> **视图切换与可见性的坑**：
> ① 视图切换是左栏 `ListBox`（`PluginsViewList`），在里面找 `ListItem` 即可；**只在左栏列表内找**，
>    否则“安装新插件”会跟别处的同名文本/按钮撞名。
> ② 未选中视图的面板 `IsVisible=false`，**其中的控件不在 UIA 树里**（与旧 FATabView 的表现一致），
>    所以查控件前必须先切到对应视图。同理，左栏里非当前视图的选项也查不到。
> ③ 2026-09-16 之前这里是 `FATabView` 页签；若以后又换回，注意 `FATabViewItem` 的 UIA `Name` 是
>    **内容类型名**（如 `Avalonia.Controls.ScrollViewer`），要按文本匹配得读它内部的 `Text`。
> ③ **窗口不在前台时 `TabItem` 根本不出现在 UIA 树里**（和顶部导航项同一个毛病）——
> 刚唤起窗口、或焦点被别的窗口抢走时，会表现为“找不到页签”而误判成页面没有页签。
> `switch-tab.ps1` 已在每次查找前先激活窗口；手写脚本时记得自己做这一步。

> **列表已虚拟化（2026-09）**：插件行列表用 `ItemsControl` + `VirtualizingStackPanel`

**批量管理（2026-09-16 新增，在「已安装」视图）**：每行左侧有选择框，顶部一条批量栏（`全选` + 计数 + 四个按钮）。
要点：

- **勾选态必须存在数据对象上**（`PluginEntry.IsSelected`，实现了 `INotifyPropertyChanged`）——
  列表虚拟化会回收容器，把 `IsChecked` 存在控件上会错位/丢状态。
  验证手法：勾几行 → `ScrollPattern` 滚到底再回顶 → **计数不变**即证明状态存活在数据上。
- **两份列表共用同一批实例**（左列是从右列筛出 `IsInstalled` 的），所以左右勾选天然同步。
- **全选只作用于当前可见的**（受 `EntryFilterBox` 影响）——验证时用**窄关键词**（如 `plugin-timer`
  能筛到 1 条），否则 `dsh-` 这类词几乎匹配全部 152 条，测不出差别。
- 按钮可用性由选中项的**能力**决定（`CanToggle` / `HasOverride` / `CanUninstall`），
  读数在 `BatchCountText` 里的 `可启停 A · 可恢复 B · 可卸载 C`。
- **验收批量写文件是否“只写一次”**：看日志。批量只打一行 `批量禁用 N 个条目`；
  逐条调 `SetEnabled` 会打 N 行 `禁用插件条目 <id>` ⇒ **断言后者为 0 行**即可证明。
- 批量卸载会弹 `FAContentDialog`（标题 `卸载 N 个插件?`、主按钮 `卸载 N 个`）：
  它是 overlay，用 `ControlType=Text` 找标题文本、`ControlType=Button` 找主按钮即可。

> （`ItemsPanel="{StaticResource PluginListPanel}"`，三处列表共用）。**只有可视区域内的行才在 UIA 树里** ——
> 实测 152 条组合条目时 UIA 里只有 **6** 个启停按钮（虚拟化前是 152）。由此产生两条验证规矩：
> ① **不能用 UIA 元素个数代表数据条数**，要读 `AllEntriesCountText` 之类的计数文本；
> ② **要操作某一行，先用 `EntryFilterBox` 把它筛出来**（或 `ScrollPattern.SetScrollPercent` 滚到它），
> 否则“按按钮文案找按钮”会找不到或者拿到别的行。

> **关闭浮窗的三种途径**（都要能工作）：① 再点页头「输出」按钮；② 点内容区空白（`PluginLogDismissLayer`）；
> ③ 按 Esc（隧道阶段处理并 `e.Handled = true`，避免冒泡到 FluentAvalonia 的导航控件）。
> ⚠ 验证 ② 只能用真实鼠标点击（该层无 UIA 对等体、无 Invoke 模式），验证 ③ 要用 `keybd_event` 而不是 `SendKeys`。
>
> **验证日志必须先打开浮窗**：浮窗默认 `IsVisible=False`（`PluginLogTextBlock` 等**都不在 UIA 树里**）。
> 断言写法：先 `InvokePattern.Invoke()` 点 `PluginLogToggleButton`,再用
> `FindFirst(..., AutomationIdProperty = 'PluginLogTextBlock')` 读 `.Current.Name`。
> 判断开/关状态用 `PluginLogScroll` 在不在，而不是 `PluginLogFlyout`（Border 没有 automation peer）。
>
> **浮窗会自动弹出**：安装/卸载一开始（`PluginService.IsBusy`）就自动打开浮窗，好让用户立刻看到进度与报错。
> 想测“关浮窗后是否提示新输出”，要用**不置 busy 的日志**（例如市场搜索、启停写回），否则会被自动弹出干扰。

> 插件启停的真实副作用是写 `%USERPROFILE%\.dsh\profiles\web\cordis.patch.yml`。
> **验收要看文件 + `dsh --profile web --dump-config`**，不能只看按钮文案：
> 点「启用/禁用」→ 托管区块里出现 `- id: <id>` + `disabled: true|false`；
> 点「恢复默认」→ 整块消失、文件变回只有注释 + `[]`；
> 最后跑一次 `dsh --profile web --dump-config`，**退出码必须是 0**（写坏的 YAML 会让整个 profile 起不来）。

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
> **验证“环境设置真的传给了子进程”**（都实测过，且无副作用）：
> ① registry：在插件页手动安装一个**不存在的包**，pnpm 的报错会带上实际请求地址 ——
>    `ERR_PNPM_FETCH_404 GET https://repo.huaweicloud.com/repository/npm/<pkg>: Not Found - 404`
>    （选“使用配置源”时这里会变成你 .npmrc 里的源）。
> ② 代理：把代理写成非法值（如 `127.0.0.1:9`）再跑同一条命令，报错变成
>    `ERR_PNPM_META_FETCH_FAIL … Couldn't parse proxy URL` / HttpClient 侧是
>    `由于目标计算机积极拒绝，无法连接。 (127.0.0.1:9)`。
> ③ 无副作用自查：失败安装不会改 profile —— 事后比对 `~/.dsh/profiles/web/package.json` 与
>    `pnpm-lock.yaml` 的哈希；`HttpClient` 代理改后**免重启**（同一次运行里点「刷新目录」即可成功）。


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
7. 主界面可能隐藏在托盘：先 `Start-Process` 再启一个实例，借单实例机制唤起。
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
14. **`FATabViewItem`（插件页的页签）的 UIA `Name` 是内容类型名**（如 `Avalonia.Controls.ScrollViewer`），不是页签文本；
    要按文本找页签就得先找 `ControlType.TabItem`，再读它内部的 `Text`。好消息是它**支持 `SelectionItemPattern.Select()`**，
    不像顶部 `FANavigationView` 那样必须真实鼠标点击。
    **但页签只在窗口处于前台时才以 `TabItem` 暴露**（同坑 1）：刚唤起窗口后直接找页签会一无所获，
    表现为超时报“找不到页签”。先 `SetForegroundWindow` + `ShowWindow(SW_RESTORE)` 激活一次再找。
15. **未选中页签的内容 `IsVisible=false`，不在 UIA 树里**（同理 `CustomRegistryBox` 只在选“自定义”时出现）。
    查控件前必须先切到对应页签，否则会误判成“控件不存在”。
16. **改完 AXAML 若报“找不到某个事件处理器”，先构建一次再判断**：AXAML 分析器的报错可能是上一版的陈旧结果
    （code-behind 里明明有对应方法，`get_errors` 仍报缺失）。
17. **`SetWindowPos` 置顶时必须带 `SWP_NOMOVE(0x0002)`**（`SWP_NOSIZE|SWP_NOMOVE|SWP_SHOWWINDOW` = `0x0043`）。
    只写 `SWP_NOSIZE` 会把窗口**移动到 (0,0)** —— 而应用会把窗口位置写进 `window-state.json`，
    等于改坏用户的窗口布局（本次就踩到了，窗口被压到最小尺寸停在左上角）。截完图记得把窗口恢复原样。
18. **`Border` / `Grid` / `ItemsControl` / `Ellipse` 都没有 automation peer**：即使设了 `x:Name` 也不会出现在 UIA 树里。
    需要判断这类元素的存在/显隐时，用它的**子元素**当锚点（例如用 `PluginLogScroll` 判断日志浮窗开没开，
    而不是 `PluginLogFlyout`）。
19. **虚拟化列表里只有可视行在 UIA 树里**（插件页三个列表都是）。所以：UIA 里的元素个数**不等于**数据条数；
    要点击屏幕外某一行，必须先用筛选框缩窄，或把列表滚到那一行。
    自查方法：读 `AllEntriesCountText`（`共 N 条` / `显示 N 条 / 共 M 条`）对照 UIA 里实际的行数。
20. **不要用 `SendKeys` 去发按键收弹层**：它会把按键广播给当前活动窗口，不可控。（曾误以为按 Esc 会让应用跳回首页，
    后来用 `keybd_event` 精确复测 —— 应用里**根本没有** `Key.Escape` 处理，Esc 在浮窗关闭时按下去毫无反应，
    当时的“跳回首页”是误判。要发按键就用 `keybd_event`/`SendInput`，且先把目标窗口置顶。
21. **用 `MoveWindow` 改窗口尺寸后，尺寸会在后续 Show/布局时漂移**（实测 1680x945 → 1733x993 → 2207x1034），
    所以“缩到最小窗口看效果”这类实验**必须显式恢复一次原尺寸**再结束。
    好消息：`%APPDATA%\DSH Launcher\Settings\window-state.json` **只在隐藏/退出时才写**，
    所以这些截图实验不会污染用户持久化的窗口几何。
22. **截图前要把窗口真正抬到最前**：只 `SetWindowPos(TOPMOST)` 有时不够 —— 实测截到过浏览器/VSCode 的像素
    （窗口其实在后面）。先 `ShowWindow(SW_RESTORE)` + `SetForegroundWindow` 再置顶，**拷贝前重新读一次**
    `BoundingRectangle`，并顺便打印 `GetForegroundWindow()` 是否就是该窗口来自查。
23. **Fluent 的滚动条是 overlay（画在内容之上，不占布局）**：不给内容留右槽位就会盖住控件右边缘。
    插件页踩过三处：左栏滚动的选项（下拉/按钮）、**列表行尾的按钮**（行模板 `Padding` 右只有 8）、日志文本。
    修法：给滚动内容加右边距（左栏 14 / 行模板 16 / 日志 12），并且**把容器宽度补回来**
    （左栏 240 → 254，保证控件净宽仍是 214）。
    自查方法：截图后用 `GetPixel` 沿一行扫出与底色不同的列区间，看控件右边界与滚动条位置是不是分开了。
24. ⚠ **Avalonia 的 `ComboBox` 会在构造时自动选中第 0 项**（即使 XAML 没写 `SelectedIndex`），
    那个 `SelectionChanged` 发生在 `InitializeComponent` 期间、**早于** `AttachedToVisualTree`。
    如果处理程序里会写设置，就会把用户的选择静默改成第 0 项（插件页实测把 `dsh-plugin.org` 改成了 `Official`）。
    修法：加一个 `_pageReady` 标志，在进页面、回填完毕后才置位；处理程序开头 `if (!_pageReady) return;`
    （光靠 `_initializing` 不够 —— 它要到进页面时才置位）。
    验证手法：把设置改成**非第 0 项**的值 → 启动 → 只进一次页面 → 看 `settings.json` 有没有变。
25. **慢速异步加载 + 可切换的输入源 = 必须处理“晚到的旧响应”**（插件页切目录来源踩过）：    目录要 0.1-12 秒才能拉完，用户在窗口里再切来源就会出现
    “**慢的旧来源最后落地**” —— 下拉显示 B、列表却是 A 的数据。
    两个必备手段：
    - **按输入源缓存**（服务层 `Dictionary<url, 结果>`），切回来瞬时命中。
      缓存地址只来自枚举 ⇒ 天然有界，不需要淘汰策略。
    - **请求世代号**（`int _catalogRequestId`）：每次发起（**以及命中缓存直接显示时**）都自增，
      回调里 `if (requestId != _catalogRequestId) return;` 丢弃过期结果。
    - ⚠ **不要用 `if (_loading) return;` 挡重入** —— 那会把用户的新请求直接吞掉，
      结果就是本条描述的 bug（旧结果照样落地）。正确做法是“新请求顶替旧请求”，而不是拒绝新请求。
    - ⚠ **命中缓存的分支也要自增世代号**：否则「A→B→A」时 A 命中缓存秒显，
      随后在飞的 B 完成（号未变）会把列表改成 B。
    - 过期请求的结果**仍然留在缓存里**（先存后丢），所以“被顶替时白拉了”不成立，下次切回来就命中。
26. ⚠ **不要靠肉眼判断“日志里是不是乱码”**：终端会把 **U+2009（窄空格）** 与 **U+2715（✕）** 渲染成 `?`，
    看起来像解码失败，其实是终端字体缺字。实测险些因此误报“还有残留乱码”。
    正确做法：**按码点断言** —— 读 `PluginLogTextBlock` 的 `Name`，检查 `Contains([char]0x2009)` / `Contains([char]0x2715)` / `Contains([char]0x2514)` 等，
    再用 `[regex]::Match($log, '.{0,2}WARN.{0,2}').Value` 把码点列出来看。
    拿不准就去抓**原始字节**（子进程输出按 Latin1 收，打印十六进制），比猜字符靠谱。
27. **子进程输出编码：UTF-8 与系统代码页会混在同一条流里**（插件安装日志乱码踩过）：
    - `ProcessStartInfo` 开了重定向但**没设 `StandardOutputEncoding`** 时，.NET 回退到系统 ANSI 代码页
      （中文 Windows = GBK/936）。
    - 而 **Node 系工具（dsh / npm / pnpm）写 UTF-8** ⇒ 必然乱码：`…WARN…` → `鈥塛ARN鈥?`、`└─┬` → `鈹溾攢鈹?`、`✕` → `鉁?`。
    - **但不能一刀切换成 UTF-8**：`cmd.exe` 自己的消息（如 `'xxx' 不是内部或外部命令`）是 **OEM 代码页**，
      且两种输出会落在**同一管道（甚至同一 stderr）**上。实测 `chcp 65001` 对**重定向的 cmd** 输出**无效**。
    - 解法：重定向编码用 **`Encoding.Latin1`**（字节↔字符无损，当“原样字节”通道），
      拿到字符串后再逐行判定：**先试严格 UTF-8**（`new UTF8Encoding(false, throwOnInvalidBytes: true)`），
      抛 `DecoderFallbackException` 就回退 OEM 代码页。GBK 第二字节常落在 0x40-0x7F，不是合法 UTF-8 续字节，所以判定很可靠。
    - 验证：装一个带 peer 依赖的包（如 `dsh-context`），pnpm 会输出带盒线字符与窄空格的警告树；
      **测完记得卸载还原**，并确认 `package.json` 的 `dependencies` 回到原样。
28. **`CheckBox` 不支持 `InvokePattern`，只能用 `TogglePattern`**（批量栏的「全选」踩过）：
    对着 CheckBox 取 `InvokePattern` 会抛「不支持的模式」。另外**部分元素取 Pattern 也会失败**
    （离屏/未实现的虚拟化行），遍历时要逐个 `try/catch` 跳过，不要假设全都能交互。
29. **筛选/查找元素时别只看 `BoundingRectangle`**：虚拟化的列表会把**视口外但仍已实现**的行也报出有效矩形
    （实测 8 个可见行会查到 21 个选择框）。要更可靠就**先用筛选框把列表缩到很小**再遍历。
30. **PowerShell 里全角引号 `“”` 也是字符串定界符**：在双引号字符串里写 `“批量禁用”` 会导致解析报
    「意外的标记」。中文断言标签请改用 `「」`。

## 验证完记得收尾

```powershell
Stop-Process -Name "DSH Launcher" -Force
# 恢复被测试改动的设置,并重启应用
# 删除本次创建的一次性临时脚本
```

页面切换脚本与页签切换脚本都是**常驻工具**（本 skill 的 `scripts/`），不要当作一次性脚本删掉。
