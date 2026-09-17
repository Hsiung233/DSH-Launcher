# DSH Launcher

[DeepSeek Shell (dsh)](https://www.npmjs.com/package/@deepseek-ai/dsh) 的桌面启动器 —— 用 Avalonia 打造的 Windows 图形化外壳。

它把 `dsh` 的安装、启停、Web 界面访问、插件管理这些命令行操作，收进一个常驻系统托盘的 WinUI 风格窗口里。

![DSH Launcher 首页：服务运行中，底部实时显示 dsh web 的输出](docs/screenshots/home.png)

---

## 功能特性

### 服务管理
- **一键安装 / 更新**：探测 `dsh` 是否安装，显示已安装版本与 npm 上的最新版本，有更新时给出「更新」按钮。
- **启动 / 停止 / 重启**：通过 `dsh web` 启动服务，自动从输出中解析 Web 地址（形如 `http://127.0.0.1:3080/?token=...`）。
- **端口配置**：可自定义监听端口（1–65535），留空使用 dsh 默认的 `3080`。
- **进程树清理**：停止时连同子进程一起终止（Windows 用 Job Object，其他平台用 `Kill(entireProcessTree)`），应用退出/系统关机时也会兜底停止服务。
- **实时日志**：`dsh web` 的标准输出实时显示在首页日志卡片中，支持复制、清空、在文件管理器中定位日志文件。

### 环境信息
首页可展开的面板，展示运行状态（PID / 启动时刻）、应用版本、已安装版本、Node.js 与 npm 版本、`dsh` 可执行文件路径、WebView 引擎版本。

### Web 界面访问
- **应用内打开**：内置 WebView 窗口（Windows 走 WebView2，macOS 走 WKWebView）。
- **浏览器打开**：调用系统默认浏览器。
- **复制 URL**：把带 token 的完整地址复制到剪贴板。

WebView 做了两项体验优化：
- **窗口复用**：关闭时默认只隐藏而非销毁，再次打开是毫秒级复用（冷启动约 940ms，复用约 200ms）。
- **空闲释放**：隐藏超过设定时长（默认 5 分钟，可关闭该行为）后自动关闭并释放 WebView 进程内存。

### 插件管理
内置插件页，直接读写 dsh 的 profile（默认 `~/.dsh/profiles/web`）：

- **已安装的插件**：列出当前 profile 的 `dependencies`，支持**启用 / 禁用**（通过往 `cordis.patch.yml` 写定向覆盖实现）、**卸载**、**恢复默认**。
- **全部组合条目**：展示 `dsh --profile web --dump-config` 输出的完整组合清单（含自带 bundle），可搜索筛选。
- **批量操作**：每行勾选框 + 批量栏，支持全选（作用于当前筛选结果）、批量启用/禁用/卸载/恢复。
- **安装新插件**：从社区策展目录搜索并一键安装（转发给 `dsh plugin --profile web add ...`）。
  - 支持两份目录：默认 `awesome-dsh-plugin.com`（约 3700 条），或 `dsh-plugin.org`（约 9300 条，带人工验证标记）。目录结构自动识别，按地址缓存，可手动刷新。
  - 也支持手动输入包名/`github:owner/repo` 规格安装。
- **安装输出**：浮动日志窗实时显示安装/卸载输出；安装开始时自动弹出，关闭后有新日志会显示未读小圆点。

![插件页：已安装的插件与全部组合条目，顶部是批量操作栏](docs/screenshots/plugins-installed.png)

![插件页：从社区目录浏览并安装新插件](docs/screenshots/plugins-catalog.png)

### 系统托盘
- 单击 / 双击托盘图标可分别配置为：在 WebView 中打开、在浏览器中打开、显示主界面、无动作。
- 关闭主窗口默认最小化到托盘（可配置为直接退出）。
- 托盘右键菜单可控制服务启停与退出程序。

### 设置
WinUI 卡片式设置页，分四组：

| 卡片 | 设置项 |
|---|---|
| **服务** | 启动时自动运行服务、服务就绪后的动作、关闭主窗口时隐藏到托盘、监听端口 |
| **系统托盘** | 单击动作、双击动作 |
| **WebView** | 应用内链接打开方式（系统浏览器 / 应用内）、关闭时保留窗口、保留超时（分钟） |
| **环境** | npm 源（使用配置源 / 官方 / npmmirror / 腾讯云 / 华为云）、HTTP 代理、不走代理的地址 |

环境设置会注入到 `npm` / `pnpm` / `dsh plugin` 等子进程，以及插件目录下载所用的 HTTP 客户端；**不影响已在运行的服务**。

![设置页：服务与环境](docs/screenshots/settings-service.png)

![设置页：系统托盘与 WebView](docs/screenshots/settings-webview.png)

### 窗口状态记忆
自动记住主窗口与 WebView 窗口的位置、尺寸、最大化状态，下次启动还原。

---

## 技术栈

| 项 | 版本 |
|---|---|
| .NET | `net10.0`（SDK 10.0.401） |
| Avalonia | 12.1.1 |
| Avalonia.Controls.WebView | 12.1.0 |
| FluentAvaloniaUI | 3.1.0（WinUI 风格控件与主题） |
| CommunityToolkit.Mvvm | 8.4.2 |

架构上刻意保持简单：**无 DI 容器、无 MVVM 框架**，UI 采用 code-behind + `x:Name`，业务逻辑集中在 `Services/` 下的单例服务中。服务事件在后台线程触发，UI 侧统一通过 `Dispatcher.UIThread.Post` 回到 UI 线程。

---

## 构建与运行

```powershell
# 构建
dotnet build "DSH Launcher.slnx"

# 运行（开发调试）
dotnet run --project "DSH Launcher/DSH Launcher.csproj"
```

> 构建前建议先结束正在运行的实例（`Stop-Process -Name "DSH Launcher"`），否则输出程序集可能被占用。

发行版另附 `Properties/PublishProfiles/DSH Launcher_Windows_x64.pubxml` 发布配置。

### 打包安装程序（Inno Setup）

1. 发布应用（框架依赖版，要求用户机器已装 [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)）：

   ```powershell
   dotnet publish "DSH Launcher/DSH Launcher.csproj" -p:PublishProfile="DSH Launcher_Windows_x64" -c Release
   ```

   输出目录为 `DSH Launcher/bin/Publish/DSH Launcher_Windows_x64`（与 `installer.iss` 里的
   `MyPublishDir` 对应）。

2. 用 [Inno Setup 6](https://jrsoftware.org/isinfo.php) 打开 `Setup/installer.iss` 编译，
   输出 `Setup/DSHLauncher-Setup-x64.exe`。

安装程序内置 .NET 10 Desktop Runtime 检测：缺失时引导用户到官网下载后再装；支持中文向导、
开始菜单/桌面快捷方式与卸载。

安装/卸载时会检测应用的单实例 Mutex，若应用正在运行（常驻托盘）会提示先关闭，避免文件被占用。
**卸载会一并清理用户数据**（`%APPDATA%\DSH Launcher` 与 `%LOCALAPPDATA%\DSH Launcher` 下的设置、日志、
窗口状态记忆，以及 WebView2 缓存），不留残留。

---

## 目录结构

```
DSH Launcher.slnx                 解决方案（.slnx 格式）
DSH Launcher/
  App.axaml(.cs)                  应用入口：主题、托盘、单实例、退出清理
  Program.cs                      程序入口
  app.manifest                    Windows 清单（DPI 感知等）
  Assets/                         图标资源（logo.ico / logo-512.png）
  Views/                          界面
    MainWindow.axaml(.cs)         主窗口 + 顶部导航（首页/插件/设置）
    HomePageControl.axaml(.cs)    首页：状态、启停、端口、日志、环境信息
    PluginsPageControl.axaml(.cs) 插件页：已安装 / 安装新插件
    SettingsPageControl.axaml(.cs)设置页：服务 / 托盘 / WebView / 环境
  Services/                       业务服务（均为单例）
    DshService.cs                 核心：安装、启停、日志、版本检查、环境探测
    DshService.JobObject.cs       Windows Job Object，保证子进程随主进程退出
    PluginService.cs              插件清单读写、启停覆盖、目录拉取、安装卸载
    SettingsService.cs            设置模型与 JSON 持久化（含宽容枚举解析）
    ChildEnvironment.cs           npm 源 / 代理注入子进程与 HttpClient
    PlatformProcess.cs            跨平台进程启动、命令定位、路径与输出解码
    WebOpener.cs                  WebView 窗口管理、浏览器打开、引擎探测
    WindowStateService.cs         窗口位置/尺寸/最大化状态记忆
    TrayService.cs                系统托盘图标与交互
    AppLogService.cs              应用日志文件
  Properties/PublishProfiles/     dotnet publish 发布配置（DSH Launcher_Windows_x64）

Setup/                            Inno Setup 安装包脚本（installer.iss）与产物输出
docs/screenshots/                 README 中使用的界面截图
```

---

## 数据与配置位置

| 内容 | Windows | macOS |
|---|---|---|
| 设置 | `%APPDATA%\DSH Launcher\Settings\settings.json` | `~/Library/Application Support/DSH Launcher/Settings/settings.json` |
| 应用日志 | `%LOCALAPPDATA%\DSH Launcher\Settings\app.log` | 同上目录下 `app.log` |
| 窗口状态 | 同上目录下 `window-state.json` | 同上 |

设置文件只在有改动时写回；新增字段在用户首次修改前不会出现在文件里。枚举值按名称解析，删除枚举成员不会导致已有设置被重置。

被管理的 dsh profile 位于 `~/.dsh/profiles/web`，插件启停通过改写该目录下的 `cordis.patch.yml` 实现。

---

## 已知限制

- **平台**：主要在 Windows 上开发与验证。代码已做了大量跨平台抽象（进程启动、路径、输出编码、WebView 引擎探测、数据目录、系统浏览器打开），但 macOS 侧仍缺：应用打包（`.app` bundle + `Info.plist` + ATS 例外 + 签名公证）、更惯用的单实例机制、以及窗口状态中若干 Win32 坐标假设的实测校正。
- **监听地址固定为 `127.0.0.1`**：`dsh web` 的 `--host` 实际被上游硬拦截（仅接受 `127.0.0.1` / `0.0.0.0`，且 `0.0.0.0` 被启动脚本以安全理由拒绝），因此启动器不提供监听地址配置。
- **插件启停非官方 API**：`dsh` 目前没有插件启停的官方接口，启动器是通过写 `cordis.patch.yml` 实现的，属自行实现（写入区域有注释标记包裹）。
- **pnpm 依赖**：安装/卸载插件需要可用的 `pnpm`。界面会探测并在未就绪时给出提示（启用/禁用不依赖 pnpm）。

---

## 相关链接

- 被管理的服务：[`@deepseek-ai/dsh`](https://www.npmjs.com/package/@deepseek-ai/dsh)
- 界面框架：[Avalonia](https://avaloniaui.net/)
- WinUI 风格控件：[FluentAvaloniaUI](https://github.com/amwx/FluentAvalonia)
- 插件目录：[awesome-dsh-plugin](https://awesome-dsh-plugin.com/) · [dsh-plugin.org](https://dsh-plugin.org/)

---

## 许可证

本项目采用 [MIT 许可证](LICENSE)。
