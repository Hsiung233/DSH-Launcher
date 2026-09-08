using System;
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

        /// <summary>请求跳转到设置页(由主窗口订阅)。</summary>
        public event Action? SettingsNavigationRequested;

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

            this.InstallButton.IsVisible = !installed;
            this.InstallButton.IsEnabled = !installing;
            this.InstallButtonText.Text = installing ? "安装中..." : "安装";

            this.SettingsButton.IsVisible = installed;
            this.SettingsButton.IsEnabled = !installing;

            this.RunButton.IsVisible = installed && !running;
            this.StopButton.IsVisible = installed && running;
            this.RestartButton.IsVisible = installed && running;
            this.RunButton.IsEnabled = !installing;
            this.StopButton.IsEnabled = !installing;
            this.RestartButton.IsEnabled = !installing;

            // Web 端打开按钮:运行中且已从 stdio 检测到 Web 服务地址时显示
            var hasWebUrl = running && this._dsh.WebUrl is not null;
            this.OpenInWebViewButton.IsVisible = hasWebUrl;
            this.OpenInBrowserButton.IsVisible = hasWebUrl;
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
            Dispatcher.UIThread.Post(this.UpdateButtons);
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

        private void OnSettingsClick(object? sender, RoutedEventArgs e)
        {
            // 跳转到设置页(导航栏选中项的同步见 MainWindow)
            this.SettingsNavigationRequested?.Invoke();
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
    }
}
