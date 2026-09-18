using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using DSH_Launcher.Services;
using FluentAvalonia.UI.Controls;

namespace DSH_Launcher.Views
{
    public partial class HomePageControl : UserControl
    {
        private readonly DshService _dsh = DshService.Instance;

        // 启动失败弹窗互斥标志:内容对话框同时只能打开一个
        private bool _startFailedDialogOpen;

        // 端口控件回填期间置位,避免把“回填”当成用户修改而触发保存
        private bool _launchOptionsInitializing;

        // 环境信息:静态项(Node/npm/dsh 路径)首次查得后缓存,避免每次展开都起子进程
        private bool _environmentLoaded;
        private string? _envNode;
        private string? _envNpm;
        private string? _envDshPath;

        // 上一次已知的运行状态,用于识别“运行中 → 已停止”的转换,从而在停止后补一次更新检测
        private bool _wasRunning;

        // 端口有效范围(与 XAML 里 NumericUpDown 的 Minimum/Maximum 保持一致)
        private const int MinPort = 1;
        private const int MaxPort = 65535;

        /// <summary>日志面板的刷新节流器(间隔与“为什么不能逐行刷新”见 <see cref="LogAppendThrottle"/>)。</summary>
        private readonly LogAppendThrottle _logThrottle;

        /// <summary>三处「复制」按钮的反馈(复制成功才把按钮文字闪成「已复制」)。</summary>
        private readonly CopyFeedback _webUrlCopy;
        private readonly CopyFeedback _logCopy;
        private readonly CopyFeedback _envCopy;

        public HomePageControl()
        {
            InitializeComponent();

            this._logThrottle = new LogAppendThrottle(this.UpdateLog);
            this._webUrlCopy = new CopyFeedback(this.CopyWebUrlButtonText, "复制URL");
            this._logCopy = new CopyFeedback(this.CopyLogButtonText, "复制");
            this._envCopy = new CopyFeedback(this.CopyEnvButtonText, "复制");

            // 对应 WinUI 版的 Loaded/Unloaded:服务是单例,进程保持运行;进出页面时挂接/解除事件
            AttachedToVisualTree += OnPageAttached;
            DetachedFromVisualTree += OnPageDetached;
        }

        private async void OnPageAttached(object? sender, EventArgs e)
        {
            this._dsh.LogAppended += this.Dsh_LogAppended;
            this._dsh.StateChanged += this.Dsh_StateChanged;
            this._dsh.StartFailed += this.Dsh_StartFailed;
            this._dsh.LogsCleared += this.Dsh_LogsCleared;
            this._dsh.WebUrlDetected += this.Dsh_WebUrlDetected;

            this.UpdateLog();
            this.UpdateButtons();
            this.InitLaunchOptions();
            this._wasRunning = this._dsh.IsRunning;
            await this.RefreshStatusAsync();

            // 环境信息要起子进程查询(约 1 秒),放后台跑,不阻塞页面显示;
            // 静态项首次查得后缓存,后续进页面只重拼字符串
            _ = this.EnsureEnvironmentLoadedAsync();
        }

        private void OnPageDetached(object? sender, EventArgs e)
        {
            this._dsh.LogAppended -= this.Dsh_LogAppended;
            this._dsh.StateChanged -= this.Dsh_StateChanged;
            this._dsh.StartFailed -= this.Dsh_StartFailed;
            this._dsh.LogsCleared -= this.Dsh_LogsCleared;
            this._dsh.WebUrlDetected -= this.Dsh_WebUrlDetected;
        }

        private async Task RefreshStatusAsync()
        {
            if (this._dsh.IsInstalling)
            {
                this.UpdateButtons();
                return;
            }

            this.HeaderProgress.IsActive = true;
            await this._dsh.GetInstalledVersionAsync();
            // 运行中不检测更新(避免不必要的 npm 查询),待服务停止后再检测
            if (!this._dsh.IsRunning)
            {
                await this._dsh.CheckForUpdateAsync();
            }

            this.HeaderProgress.IsActive = false;
            this.UpdateButtons();
        }

        private void UpdateButtons()
        {
            var installed = this._dsh.InstalledVersion is not null;
            var installing = this._dsh.IsInstalling;
            var running = this._dsh.IsRunning;

            this.StatusSubText.Text = installing
                ? "正在安装..."
                : installed
                    ? $"v{this._dsh.InstalledVersion}"
                    : "未安装";
            ToolTip.SetTip(this.StatusText, installed ? $"@deepseek-ai/dsh@{this._dsh.InstalledVersion}" : "@deepseek-ai/dsh");

            // 状态徽标:已安装且不在安装中时显示(安装中状态未知,先不显示)
            var showBadge = installed && !installing;
            this.StatusBadge.IsVisible = showBadge;
            if (showBadge)
            {
                this.StatusBadgeText.Text = running ? "运行中" : "已停止";
                this.RunningDot.IsVisible = running;
                this.StoppedDot.IsVisible = !running;
            }

            // 按状态互斥显示三组按钮:未安装 / 已安装未运行 / 运行中
            this.InstallActionsPanel.IsVisible = !installed;
            this.RunActionsPanel.IsVisible = installed && !running;
            this.RunningActionsPanel.IsVisible = installed && running;

            // 有新版本时才显示“更新”按钮(仅在未运行时;运行中不允许替换正在使用的包)
            var updateAvailable = installed && !running && this._dsh.IsUpdateAvailable;
            this.UpdateButton.IsVisible = updateAvailable;
            this.UpdateButton.IsEnabled = !installing;
            if (updateAvailable)
            {
                ToolTip.SetTip(
                    this.UpdateButton,
                    $"发现新版本 v{this._dsh.LatestVersion}(当前 v{this._dsh.InstalledVersion}),点击更新");
            }

            this.InstallButton.IsEnabled = !installing;
            this.RunButton.IsEnabled = !installing;
            this.StopButton.IsEnabled = !installing;
            this.RestartButton.IsEnabled = !installing;
            this.InstallButtonText.Text = installing ? "安装中..." : "安装";

            // Web 端操作按钮:运行中且已从 stdio 检测到 Web 服务地址时显示
            var hasWebUrl = running && this._dsh.WebUrl is not null;
            this.OpenInWebViewButton.IsVisible = hasWebUrl;
            this.OpenInBrowserButton.IsVisible = hasWebUrl;
            this.CopyWebUrlButton.IsVisible = hasWebUrl;
        }

        // ---- 监听端口设置 ----

        /// <summary>把设置中的监听端口回填到控件(回填期间不触发保存)。</summary>
        private void InitLaunchOptions()
        {
            this._launchOptionsInitializing = true;
            try
            {
                var port = SettingsService.Instance.Settings.ListenPort;
                this.PortBox.Value = port is >= MinPort and <= MaxPort ? (decimal?)port : null;
            }
            finally
            {
                this._launchOptionsInitializing = false;
            }
        }

        /// <summary>
        /// 端口变更时写回设置。留空(null)记作 0 = 使用 dsh 默认端口。
        /// 数字合法性与 1-65535 范围由控件自身的 Minimum/Maximum/ClipValueToMinMax 保证，
        /// 这里无需再解析文本。
        /// </summary>
        private void OnPortValueChanged(object? sender, NumericUpDownValueChangedEventArgs e)
        {
            if (this._launchOptionsInitializing)
            {
                return;
            }

            var value = this.PortBox.Value;
            var port = 0;
            if (value is decimal v && v >= MinPort && v <= MaxPort)
            {
                port = (int)v;
            }

            if (SettingsService.Instance.Settings.ListenPort == port)
            {
                return;
            }

            SettingsService.Instance.Update(s => s.ListenPort = port);
        }

        private void UpdateLog()
        {
            this.LogTextBlock.Text = this._dsh.LogText;
            this.ScrollLogToEnd();
        }

        private void ScrollLogToEnd()
        {
            // 低优先级等布局完成后再滚到底部
            Dispatcher.UIThread.Post(
                () => this.LogScroll.ScrollToEnd(),
                DispatcherPriority.Background);
        }

        // ---- 事件回调(可能来自后台线程,需调度到 UI 线程) ----

        private void Dsh_LogAppended(string text)
        {
            // 注意:这里**不能**直接 LogTextBlock.Text += text(理由见 LogAppendThrottle),
            // 只登记"待刷新",由节流器按固定间隔整段同步一次(文本内容从服务侧现取)。
            if (Dispatcher.UIThread.CheckAccess())
            {
                this._logThrottle.Schedule();
            }
            else
            {
                // 事件通常在后台线程触发(stdout/stderr 回调)
                Dispatcher.UIThread.Post(this._logThrottle.Schedule);
            }
        }

        private void Dsh_StateChanged()
        {
            Dispatcher.UIThread.Post(async () =>
            {
                var running = this._dsh.IsRunning;
                var wasRunning = this._wasRunning;
                this._wasRunning = running;
                this.UpdateButtons();

                // 环境信息里的“运行状态/PID/运行时长”是动态的,状态一变就同步刷新
                // (静态项已缓存,这里只是重新拼字符串,不起子进程)
                if (this._environmentLoaded)
                {
                    this.EnvTextBlock.Text = this.BuildEnvironmentText();
                }

                // 服务从“运行中”变为已停止后,补一次更新检测(运行期间不检测)
                if (wasRunning && !running && !this._dsh.IsInstalling)
                {
                    await this.RefreshStatusAsync();
                }
            });
        }

        /// <summary>启动后在观察期内意外退出(非用户手动停止),弹出错误提示。</summary>
        private void Dsh_StartFailed()
        {
            Dispatcher.UIThread.Post(async () =>
            {
                await this.ShowStartFailedDialogAsync(this._dsh.LastStartError);
            });
        }

        private void Dsh_LogsCleared()
        {
            Dispatcher.UIThread.Post(() => this.LogTextBlock.Text = string.Empty);
        }

        private void Dsh_WebUrlDetected(string url)
        {
            Dispatcher.UIThread.Post(this.UpdateButtons);
        }

        // ---- 按钮事件 ----

        private async void OnInstallClick(object? sender, RoutedEventArgs e)
        {
            this.HeaderProgress.IsActive = true;
            try
            {
                await this._dsh.InstallAsync();
                await this.RefreshStatusAsync();
            }
            finally
            {
                this.HeaderProgress.IsActive = false;
            }
        }

        /// <summary>更新到 npm 上的最新版本;完成后重新检查,按钮随之消失。</summary>
        private async void OnUpdateClick(object? sender, RoutedEventArgs e)
        {
            this.HeaderProgress.IsActive = true;
            try
            {
                await this._dsh.UpdateAsync();
                await this.RefreshStatusAsync();
            }
            finally
            {
                this.HeaderProgress.IsActive = false;
            }
        }

        private async void OnRunClick(object? sender, RoutedEventArgs e)
        {
            var ok = await this._dsh.StartAsync();
            if (!ok)
            {
                await this.ShowStartFailedDialogAsync(this._dsh.LastStartError);
            }
        }

        /// <summary>启动失败时弹出提示窗口;“复制信息”把完整错误写入剪贴板,“确定”关闭。</summary>
        private async Task ShowStartFailedDialogAsync(string error)
        {
            // 内容对话框同时只能打开一个:失败事件可能与手动点击并发,互斥防重入
            if (this._startFailedDialogOpen)
            {
                return;
            }

            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is not Window ownerWindow)
            {
                return;
            }

            this._startFailedDialogOpen = true;
            try
            {
                // 弹窗内只简要显示前几百字符,完整信息可通过“复制信息”获取
                const int PreviewLength = 600;
                var preview = error.Length > PreviewLength
                    ? error[..PreviewLength] + "\n..."
                    : error;

                var dialog = new FAContentDialog
                {
                    Title = "启动失败",
                    Content = new ScrollViewer
                    {
                        MaxHeight = 240,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                        Content = new SelectableTextBlock
                        {
                            Text = preview,
                            TextWrapping = TextWrapping.Wrap,
                            FontFamily = new Avalonia.Media.FontFamily("Consolas"),
                            FontSize = 12,
                        },
                    },
                    PrimaryButtonText = "复制信息",
                    CloseButtonText = "确定",
                };

                var result = await dialog.ShowAsync(ownerWindow);
                if (result == FAContentDialogResult.Primary)
                {
                    try
                    {
                        var clipboard = topLevel.Clipboard;
                        if (clipboard is not null)
                        {
                            var transfer = new Avalonia.Input.DataTransfer();
                            transfer.Add(Avalonia.Input.DataTransferItem.CreateText(error));
                            await clipboard.SetDataAsync(transfer);
                        }
                    }
                    catch (Exception)
                    {
                        // 剪贴板不可用时忽略
                    }
                }
            }
            catch (Exception)
            {
                // 极端情况下(如页面正在卸载)ShowAsync 可能抛异常,忽略以免崩溃
            }
            finally
            {
                this._startFailedDialogOpen = false;
            }
        }

        private void OnStopClick(object? sender, RoutedEventArgs e)
        {
            this._dsh.Stop();
        }

        private async void OnRestartClick(object? sender, RoutedEventArgs e)
        {
            var ok = await this._dsh.RestartAsync();
            if (!ok)
            {
                await this.ShowStartFailedDialogAsync(this._dsh.LastStartError);
            }
        }

        /// <summary>在应用内 WebView 窗口中打开 Web 端对话页面。</summary>
        private void OnOpenInWebViewClick(object? sender, RoutedEventArgs e)
        {
            if (this._dsh.WebUrl is string url)
            {
                WebOpener.OpenInWebView(url);
            }
        }

        /// <summary>在系统默认浏览器中打开 Web 端对话页面。</summary>
        private void OnOpenInBrowserClick(object? sender, RoutedEventArgs e)
        {
            if (this._dsh.WebUrl is string url)
            {
                WebOpener.OpenInBrowser(url);
            }
        }

        // ---- 复制与日志工具 ----

        /// <summary>复制含 token 的完整 Web 地址。</summary>
        private async void OnCopyWebUrlClick(object? sender, RoutedEventArgs e)
        {
            if (this._dsh.WebUrl is not string url)
            {
                return;
            }

            await this._webUrlCopy.CopyAsync(this, url);
        }

        /// <summary>复制日志面板中的全部内容。</summary>
        private async void OnCopyLogClick(object? sender, RoutedEventArgs e)
        {
            await this._logCopy.CopyAsync(this, this._dsh.LogText);
        }

        /// <summary>清空日志面板(仅面板;app.log 文件不受影响)。</summary>
        private void OnClearLogClick(object? sender, RoutedEventArgs e) => this._dsh.ClearLog();

        /// <summary>
        /// 在系统文件管理器中定位 app.log。面板里只有 dsh 的 stdio,
        /// 而 app.log 还包含面板里没有的记录:设置加载、版本检查、WebView 生命周期、启动失败详情。
        /// Windows 用 explorer /select,macOS 用 open -R(见 <see cref="PlatformProcess"/>)。
        /// </summary>
        private void OnOpenLogFileClick(object? sender, RoutedEventArgs e)
        {
            try
            {
                // 先写一行,确保文件存在——否则“在文件夹中显示”无处可选中
                AppLogService.Write("[应用] 用户请求打开日志文件");
                Process.Start(PlatformProcess.CreateRevealInFileManagerStartInfo(AppLogService.LogFilePath));
            }
            catch (Exception ex)
            {
                AppLogService.Write($"[应用] 打开日志文件失败: {ex.Message}");
            }
        }

        // ---- 环境与详情 ----

        /// <summary>
        /// 查询环境信息(Node/npm 版本、dsh 路径)。首次需起子进程(约 1 秒),之后用缓存;
        /// 刷新时只重新拼字符串,供页面加载与状态变化共用。
        /// </summary>
        private async Task EnsureEnvironmentLoadedAsync()
        {
            if (this._environmentLoaded)
            {
                this.EnvTextBlock.Text = this.BuildEnvironmentText();
                return;
            }

            this.EnvProgress.IsActive = true;
            try
            {
                var (node, npm, dshPath) = await this._dsh.GetEnvironmentInfoAsync();
                this._envNode = node;
                this._envNpm = npm;
                this._envDshPath = dshPath;
                this._environmentLoaded = true;
            }
            finally
            {
                this.EnvProgress.IsActive = false;
            }

            this.EnvTextBlock.Text = this.BuildEnvironmentText();
        }

        /// <summary>拼出便于阅读与粘贴的环境信息文本。</summary>
        private string BuildEnvironmentText()
        {
            var dsh = this._dsh;

            string state;
            if (!dsh.IsRunning)
            {
                state = "已停止";
            }
            else
            {
                // 用“启动时刻”而非“已运行时长”——后者会随时间变陈旧(本面板只在状态变化时刷新)
                var parts = new List<string>();
                if (dsh.ProcessId is int pid)
                {
                    parts.Add($"PID {pid}");
                }
                if (dsh.StartedAtLocal is DateTime startedAt)
                {
                    parts.Add($"启动于 {startedAt:HH:mm:ss}");
                }

                state = parts.Count > 0 ? $"运行中 ({string.Join("，", parts)})" : "运行中";
            }

            var appVersion = typeof(HomePageControl).Assembly.GetName().Version?.ToString() ?? "未知";

            var lines = new[]
            {
                $"运行状态 : {state}",
                $"应用版本 : {appVersion}",
                $"安装版本 : {(dsh.InstalledVersion is string iv ? "v" + iv : "未安装")}",
                $"Node.js  : {this._envNode ?? "未知"}",
                $"npm      : {this._envNpm ?? "未知"}",
                $"dsh 路径 : {this._envDshPath ?? "未找到"}",
                $"{WebOpener.EngineDisplayName} : {WebOpener.DescribeWebViewRuntime()}",
            };

            return string.Join(Environment.NewLine, lines);
        }

        /// <summary>复制环境信息。</summary>
        private async void OnCopyEnvClick(object? sender, RoutedEventArgs e)
            => await this._envCopy.CopyAsync(this, this.BuildEnvironmentText());
    }
}
