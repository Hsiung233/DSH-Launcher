using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
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

        // 上一次已知的运行状态,用于识别“运行中 → 已停止”的转换,从而在停止后补一次更新检测
        private bool _wasRunning;

        public HomePageControl()
        {
            InitializeComponent();

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

            this.StatusText.Text = installing
                ? "正在安装 @deepseek-ai/dsh..."
                : installed
                    ? $"@deepseek-ai/dsh v{this._dsh.InstalledVersion}{(running ? "  (运行中)" : "")}"
                    : "未安装";
            ToolTip.SetTip(this.StatusText, installed ? $"@deepseek-ai/dsh@{this._dsh.InstalledVersion}" : "@deepseek-ai/dsh");

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

            // Web 端打开按钮:运行中且已从 stdio 检测到 Web 服务地址时显示
            var hasWebUrl = running && this._dsh.WebUrl is not null;
            this.OpenInWebViewButton.IsVisible = hasWebUrl;
            this.OpenInBrowserButton.IsVisible = hasWebUrl;
        }

        // ---- 监听端口设置 ----

        /// <summary>把设置中的监听端口回填到控件(回填期间不触发保存)。</summary>
        private void InitLaunchOptions()
        {
            this._launchOptionsInitializing = true;
            try
            {
                var port = SettingsService.Instance.Settings.ListenPort;
                this.PortTextBox.Text = port is >= 1 and <= 65535
                    ? port.ToString(CultureInfo.InvariantCulture)
                    : string.Empty;
            }
            finally
            {
                this._launchOptionsInitializing = false;
            }
        }

        /// <summary>把控件当前值写回设置。非法端口视为“留空”,回退为 dsh 默认端口。</summary>
        private void CommitLaunchOptions()
        {
            if (this._launchOptionsInitializing)
            {
                return;
            }

            var text = (this.PortTextBox.Text ?? string.Empty).Trim();
            var port = 0;
            if (text.Length > 0)
            {
                if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                    && parsed is >= 1 and <= 65535)
                {
                    port = parsed;
                }
                else
                {
                    // 非法输入:清空为“留空”,避免把无效值写进设置
                    this._launchOptionsInitializing = true;
                    this.PortTextBox.Text = string.Empty;
                    this._launchOptionsInitializing = false;
                }
            }

            if (SettingsService.Instance.Settings.ListenPort == port)
            {
                return;
            }

            SettingsService.Instance.Update(s => s.ListenPort = port);
        }

        private void OnPortTextInput(object? sender, TextInputEventArgs e)
        {
            // 端口只允许数字:在输入阶段就拦截非数字字符
            if (!string.IsNullOrEmpty(e.Text) && !e.Text.All(char.IsAsciiDigit))
            {
                e.Handled = true;
            }
        }

        private void OnPortLostFocus(object? sender, RoutedEventArgs e)
        {
            this.CommitLaunchOptions();
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
            Dispatcher.UIThread.Post(() =>
            {
                this.LogTextBlock.Text += text;
                this.ScrollLogToEnd();
            });
        }

        private void Dsh_StateChanged()
        {
            Dispatcher.UIThread.Post(async () =>
            {
                var running = this._dsh.IsRunning;
                var wasRunning = this._wasRunning;
                this._wasRunning = running;
                this.UpdateButtons();

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
            // 端口输入框可能仍是焦点(未触发 LostFocus),启动前先提交一次,确保用的是界面上看到的值
            this.CommitLaunchOptions();

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
    }
}
