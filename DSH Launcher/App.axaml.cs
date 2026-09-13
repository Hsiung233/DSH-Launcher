using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using DSH_Launcher.Services;
using DSH_Launcher.Views;

namespace DSH_Launcher;

public partial class App : Application
{
    private MainWindow? _window;
    private TrayService? _tray;

    /// <summary>true 表示用户已从托盘菜单选择退出,此时窗口关闭不再拦截。</summary>
    private bool _exitRequested;

    // 单实例协调:互斥体占位 + 事件通知已有实例显示主界面
    private const string SingleInstanceMutexName = "DSH_Launcher_SingleInstance";
    private const string ShowMainWindowEventName = "DSH_Launcher_ShowMainWindow";
    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _showMainWindowEvent;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 托盘常驻应用必须显式控制退出:默认 OnLastWindowClose 模式下,主窗口隐藏启动时
            // (未 Show 过,不在 Windows 集合),关闭唯一的 WebView 窗口会被判为"最后一个窗口
            // 关闭"而整体退出应用。改为仅在托盘菜单"退出程序"中显式调用 Shutdown()。
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // 系统关机/注销等外部退出路径:同样要放行 WebView 窗口关闭,
            // 否则“关闭即隐藏”的拦截会挡住退出
            desktop.ShutdownRequested += (_, _) => WebOpener.BeginShutdown();

            // 单实例:已有实例在运行时,通知其显示主界面,然后退出当前进程
            _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out var createdNew);
            if (!createdNew)
            {
                try
                {
                    using var evt = EventWaitHandle.OpenExisting(ShowMainWindowEventName);
                    evt.Set();
                    AppLogService.Write("[启动] 检测到已有实例,已通知其显示主界面");
                }
                catch (Exception)
                {
                    // 打开事件失败时直接退出,不影响已有实例
                }

                Environment.Exit(0);
                return;
            }

            _showMainWindowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowMainWindowEventName);
            _ = Task.Run(WaitForShowMainWindowRequests);

            // 记录应用启动标记到文件日志(%LOCALAPPDATA%\DSH Launcher\Settings\app.log)
            AppLogService.MarkSessionStart();

            // DSH 服务启动并检测到 Web 地址后,按“启动DSH服务后”设置自动打开
            DshService.Instance.WebUrlDetected += OnDshWebUrlDetected;

            _window = new MainWindow();

            // 拦截标题栏关闭按钮:隐藏到系统托盘而不是退出
            _window.Closing += OnWindowClosing;

            InitializeTrayIcon();

            // 启动时打开主界面;关闭该设置时启动到系统托盘(窗口保持隐藏,可从托盘打开)。
            // 注意:Avalonia 11.1+ 的 ClassicDesktopStyleApplicationLifetime 会在启动结束时
            // 自动调用 desktop.MainWindow.Show(),因此关闭设置时绝不能给 desktop.MainWindow
            // 赋值(托盘"打开界面"走 _window.Show(),不依赖该属性),否则主界面总会被显示。
            if (SettingsService.Instance.Settings.ShowMainWindowOnStartup)
            {
                desktop.MainWindow = _window;
                _window.Show();
            }
            else
            {
                AppLogService.Write("[启动] 按设置未打开主界面,已启动到系统托盘");
            }

            // 启动时运行 DSH 服务(后台执行,不阻塞首屏;失败仅记录日志,不弹窗)
            if (SettingsService.Instance.Settings.RunDshServiceOnStartup)
            {
                _ = AutoStartDshServiceAsync();
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>按设置在应用启动后自动运行 DSH 服务;未安装或已在运行时直接跳过。</summary>
    private static async Task AutoStartDshServiceAsync()
    {
        var dsh = DshService.Instance;
        if (dsh.IsRunning || dsh.IsInstalling)
        {
            return;
        }

        var version = await dsh.GetInstalledVersionAsync();
        if (version is null)
        {
            dsh.AppendSystemLog("[启动] 已开启“启动时运行DSH服务”,但未安装 @deepseek-ai/dsh,跳过自动运行");
            return;
        }

        dsh.AppendSystemLog("[启动] 按设置自动运行 DSH 服务");
        var ok = await dsh.StartAsync();
        if (!ok)
        {
            dsh.AppendSystemLog("[启动] 自动运行 DSH 服务失败,详情见上方日志");
        }
    }

    /// <summary>
    /// DSH 服务检测到 Web 地址后按“启动DSH服务后”设置自动打开。
    /// 无论服务是开机自动运行还是用户手动启动,一律生效。
    /// </summary>
    private static void OnDshWebUrlDetected(string url)
    {
        var action = SettingsService.Instance.Settings.AfterDshServiceStarted;
        if (action == WebOpenAction.None)
        {
            return;
        }

        AppLogService.Write($"[启动] 按设置自动打开 Web 端({action}): {url}");
        // WebUrlDetected 从 stdio 后台线程触发,而 WebOpener 会创建 UI 对象(Window),
        // 必须调度到 UI 线程,否则抛跨线程异常导致 WebView 被打开异常却退回浏览器
        Avalonia.Threading.Dispatcher.UIThread.Post(() => WebOpener.Open(action, url));
    }

    /// <summary>后台等待“显示主界面”请求(来自二次启动的进程)。</summary>
    private void WaitForShowMainWindowRequests()
    {
        while (_showMainWindowEvent is not null && _showMainWindowEvent.WaitOne())
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(ShowMainWindow);
        }
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_exitRequested)
        {
            return;
        }

        // 隐藏到托盘前记录窗口位置/大小/最大化状态,下次启动恢复
        _window?.SaveWindowState();

        // 隐藏到托盘,进程继续运行
        e.Cancel = true;
        _window?.Hide();
    }

    private void InitializeTrayIcon()
    {
        _tray = TrayService.TryCreate("DSH Launcher");
        if (_tray is not null)
        {
            _tray.MainWindowRequested += ShowMainWindow;
            _tray.WebViewOpenRequested += OnTrayWebViewOpen;
            _tray.BrowserOpenRequested += OnTrayBrowserOpen;
            _tray.SingleClickRequested += OnTraySingleClick;
            _tray.DoubleClickRequested += OnTrayDoubleClick;
            _tray.ExitRequested += ExitApplication;
            UpdateTrayDoubleClickDetection();

            // 设置变化时同步双击检测开关
            SettingsService.Instance.SettingsChanged += OnSettingsChanged;
        }
    }

    private void OnSettingsChanged()
    {
        UpdateTrayDoubleClickDetection();
    }

    /// <summary>双击行为为“无动作”时关闭托盘的双击检测,单击立即响应。</summary>
    private void UpdateTrayDoubleClickDetection()
    {
        if (_tray is not null)
        {
            _tray.EnableDoubleClickDetection =
                SettingsService.Instance.Settings.TrayDoubleClick != WebOpenAction.None;
        }
    }

    private void OnTraySingleClick()
    {
        OpenByTrayAction(SettingsService.Instance.Settings.TraySingleClick);
    }

    private void OnTrayDoubleClick()
    {
        OpenByTrayAction(SettingsService.Instance.Settings.TrayDoubleClick);
    }

    /// <summary>托盘菜单“WebView中打开”:在应用内 WebView 窗口打开 Web 端。</summary>
    private void OnTrayWebViewOpen()
    {
        OpenByTrayAction(WebOpenAction.WebView);
    }

    /// <summary>托盘菜单“浏览器中打开”:在系统默认浏览器打开 Web 端。</summary>
    private void OnTrayBrowserOpen()
    {
        OpenByTrayAction(WebOpenAction.Browser);
    }

    /// <summary>按设置打开 Web 端;服务未运行或未检测到地址时回退为打开主界面。</summary>
    private void OpenByTrayAction(WebOpenAction action)
    {
        // 无动作:什么都不做
        if (action == WebOpenAction.None)
        {
            return;
        }

        // 打开主界面不依赖 Web 服务
        if (action == WebOpenAction.MainWindow)
        {
            ShowMainWindow();
            return;
        }

        var url = DshService.Instance.WebUrl;
        if (string.IsNullOrEmpty(url))
        {
            // 服务未运行或未检测到地址时回退为打开主界面
            ShowMainWindow();
            return;
        }

        WebOpener.Open(action, url);
    }

    private void ShowMainWindow()
    {
        if (_window is null)
        {
            return;
        }

        _window.Show();
        _window.Activate();
    }

    private void ExitApplication()
    {
        _exitRequested = true;

        // 保存主窗口位置/大小/最大化状态
        _window?.SaveWindowState();

        // 保存所有已打开的 WebView 窗口位置/大小/最大化状态
        WebOpener.SaveOpenWebViewBounds();

        // 放行 WebView 窗口的真正关闭:否则“关闭即隐藏”的拦截会让 Shutdown() 关不掉窗口
        WebOpener.BeginShutdown();

        // 退出前停止 dsh 服务进程
        DshService.Instance.Stop();

        _tray?.Dispose();
        _tray = null;

        (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }
}
