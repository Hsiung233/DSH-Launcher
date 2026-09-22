using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using DSH_Launcher.Models;
using DSH_Launcher.Services;
using DSH_Launcher.Views;

namespace DSH_Launcher;

public partial class App : Application
{
    private MainWindow? _window;
    private TrayService? _tray;

    /// <summary>true 表示用户已从托盘菜单选择退出,此时窗口关闭不再拦截。</summary>
    private bool _exitRequested;

    // 单实例协调(互斥体 + 命名管道)与进程链逃逸各自独立成服务:
    // 前者见 SingleInstanceGuard,后者见 CompatChainEscape(附完整成因说明)。
    private SingleInstanceGuard? _singleInstance;
    private CancellationTokenSource? _showMainWindowPipeCts;

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
            // 否则“关闭即隐藏”的拦截会挡住退出。这同时覆盖 macOS 的 Cmd+Q/退出菜单
            // (macOS 上原生 Quit 会走 ShutdownRequested)。
            desktop.ShutdownRequested += (_, _) => WebOpener.BeginShutdown();
            desktop.ShutdownRequested += (_, _) => StopDshOnShutdown();
            desktop.ShutdownRequested += (_, _) => _showMainWindowPipeCts?.Cancel();

            // macOS 点击 Dock 图标(Reopen)时恢复主窗口;Windows/Linux 不会触发该激活类型,无副作用
            if (desktop is IActivatableLifetime activatable)
            {
                activatable.Activated += (_, e) =>
                {
                    if (e.Kind == ActivationKind.Reopen)
                    {
                        ShowMainWindow();
                    }
                };
            }

            // 单实例:已有实例在运行时,通过命名管道通知它(它按“重复启动应用时”设置响应),然后退出当前进程
            this._singleInstance = SingleInstanceGuard.Acquire();
            if (!this._singleInstance.IsPrimary)
            {
                if (StartupArguments.IsAutoStartLaunch)
                {
                    // ⚠ 自启动撞上已有实例**不要**通知:NotifyExistingInstance 会唤起已有实例的主界面,
                    // 正好违背了“自启动静默收进托盘”的意图(登录时本不该弹窗)。已有实例本来就在托盘里,
                    // 它的服务状态由它自己管,本次自启动无事可做、直接退出即可。
                    // 场景虽然边缘(登录时一般没有别的实例),但手动带 --autostart 测试时必撞。
                    AppLogService.Write("[启动] 本次由开机自启动拉起,但已有实例在运行,直接退出(不打扰已有实例)");
                }
                else if (StartupArguments.IsShowMainWindowLaunch)
                {
                    // --show-main-window:明确要求显示主界面,走专用管道(不受"重复启动应用时"设置影响)。
                    // 界面自动化验证脚本直接连那条管道;这个参数是给人工/热键工具用的同款入口。
                    this._singleInstance.NotifyShowMainWindow();
                }
                else
                {
                    this._singleInstance.NotifyExistingInstance();
                }

                Environment.Exit(0);
                return;
            }

            // 上游进程链逃逸(2026-09-18 端到端实验定位;完整成因与"不要删"的理由见 CompatChainEscape)。
            // 探测后旧实例先释放互斥体再退出,避免新实例走"通知已有实例"路径被误退。
            if (CompatChainEscape.IsInsideInheritedCompatChain())
            {
                if (CompatChainEscape.ConsumeMarker() == EscapeMarkerState.Fresh)
                {
                    // 护栏触发:本实例已经是逃逸后重开的,不再逃逸 —— 只把排查方向告诉用户
                    DshService.Instance.AppendSystemLog(CompatChainEscape.PersistentCompatLayerHint);
                }
                else
                {
                    // ⚠ 顺序不能变:先放锁,再由 explorer 拉起新实例
                    this._singleInstance.Release();
                    if (CompatChainEscape.TryRestartViaExplorer(out var failureReason))
                    {
                        Environment.Exit(0);
                        return;
                    }

                    // 逃逸失败:继续以当前实例运行(保底旧行为),并把单实例锁拿回来
                    AppLogService.Write($"[启动] 逃逸重启失败,继续以当前实例运行:{failureReason}");
                    this._singleInstance.TryReacquire();
                }
            }
            else
            {
                // 本次是干净链:上一次逃逸留下的标记已经无意义,顺手清掉(理由见 DeleteMarker)。
                // ⚠ 删之前先把"逃逸前是自启动"的语义接过来 —— 干净链实例不调 ConsumeMarker(护栏只对链内有意义),
                // 不接的话逃逸重启后的新实例会丢掉自启动行为,弹主界面。
                if (CompatChainEscape.MarkerCarriesAutoStartFlag())
                {
                    StartupArguments.MarkAutoStartLaunch();
                    AppLogService.Write("[启动] 逃逸前是自启动拉起,重启后维持自启动行为(不弹主界面)。");
                }

                CompatChainEscape.DeleteMarker();
            }

            _showMainWindowPipeCts = new CancellationTokenSource();
            _ = this._singleInstance.ListenAsync(
                _showMainWindowPipeCts.Token,
                // 全名限定:Application 自己有个实例属性叫 Dispatcher,不限定会解析成实例成员
                () => Avalonia.Threading.Dispatcher.UIThread.Post(OnRepeatLaunchRequested));

            // "无条件显示主界面"管道(--show-main-window / 界面自动化脚本用):
            // 不经"重复启动应用时"设置,收到连接就把主界面叫出来
            _ = this._singleInstance.ListenShowWindowAsync(
                _showMainWindowPipeCts.Token,
                () => Avalonia.Threading.Dispatcher.UIThread.Post(ShowMainWindow));

            // 记录应用启动标记到文件日志(%LOCALAPPDATA%\DSH Launcher\Settings\app.log)
            AppLogService.MarkSessionStart();

            // 设置层的加载结果由这里转写到日志面板:设置服务不该反过来依赖 DSH 服务(理由见 SettingsService)
            if (SettingsService.Instance.TakeLoadDiagnostic() is { } settingsDiagnostic)
            {
                DshService.Instance.AppendSystemLog(settingsDiagnostic);
            }

            // 开机自启动:把系统侧的自启动项与设置对齐(程序换了安装目录、注册项被手工删掉时在这里自愈)。
            // 放在启动流程里而不是设置页 —— 自启动要在用户还没进过任何页面时就已经是有效的。
            // 诊断信息同样由这里转写(理由同上一段:自启动服务比 DshService 更底层,不自己写日志)。
            if (AutoStartService.Instance.Reconcile() is { } autoStartDiagnostic)
            {
                DshService.Instance.AppendSystemLog(autoStartDiagnostic);
            }

            // 启动时把"兼容层/提权"相关的环境证据写进 app.log,便于事后比对两条启动路径的差异
            AppLogService.Write(ChildEnvironment.DescribeInheritedVariables());

            // 提权信息已由上面那行 app.log(DescribeInheritedVariables)留证,不再往面板里提示:
            // 实测提权与兼容层变量本身都不是 dsh 失败的原因(真因是上游进程链的链级兼容层标记;
            // 正解见 CompatChainEscape 里的"逃逸上游进程链")。

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
            //
            // 由**系统自启动**拉起时一律收进托盘(见 StartupArguments):登录就弹一个窗口是用户关掉
            // 自启动的首要原因;服务照常按设置启动,想用界面时点托盘图标即可。
            if (StartupArguments.IsAutoStartLaunch)
            {
                AppLogService.Write("[启动] 本次由开机自启动拉起,已启动到系统托盘");
            }
            else if (StartupArguments.IsShowMainWindowLaunch
                || SettingsService.Instance.Settings.ShowMainWindowOnStartup)
            {
                // 带 --show-main-window 的**首次**启动(没有已有实例可通知)同样要显示主界面,
                // 这样它永远是"确定能出窗口"的入口,调用方不必区分自己是不是第一个实例
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

    /// <summary>
    /// 外部发起的退出(系统关机、注销,以及 macOS 的 Cmd+Q/退出菜单)路径上兜底停掉 dsh 进程。
    /// Windows 上仍有作业对象兜底;macOS 无内核级兜底,这一步就是唯一防线,不能省。
    /// <see cref="DshService.Stop"/> 是幂等的(无进程时空转),与托盘“退出程序”路径重复调用无副作用。
    /// </summary>
    private static void StopDshOnShutdown()
    {
        if (DshService.Instance.IsRunning)
        {
            AppLogService.Write("[退出] ShutdownRequested:停止 dsh 服务进程");
            DshService.Instance.Stop();
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
        OpenByAction(SettingsService.Instance.Settings.TraySingleClick);
    }

    private void OnTrayDoubleClick()
    {
        OpenByAction(SettingsService.Instance.Settings.TrayDoubleClick);
    }

    /// <summary>托盘菜单“WebView中打开”:在应用内 WebView 窗口打开 Web 端。</summary>
    private void OnTrayWebViewOpen()
    {
        OpenByAction(WebOpenAction.WebView);
    }

    /// <summary>托盘菜单“浏览器中打开”:在系统默认浏览器打开 Web 端。</summary>
    private void OnTrayBrowserOpen()
    {
        OpenByAction(WebOpenAction.Browser);
    }

    /// <summary>
    /// 已有实例收到“又启动了一次启动器”的信号(命名管道)后,按“重复启动应用时”设置响应。
    /// 命名管道回调来自后台线程,调用方已调度到 UI 线程(WebView/Window 只能在 UI 线程创建)。
    /// </summary>
    private void OnRepeatLaunchRequested()
    {
        var action = SettingsService.Instance.Settings.RepeatLaunchAction;
        AppLogService.Write($"[启动] 重复启动:已有实例按设置执行 {action}");
        OpenByAction(action);
    }

    /// <summary>
    /// 按动作打开 Web 端或主界面。托盘动作与“重复启动应用时”共用这一处判定:
    /// 无动作直接返回;打开主界面不依赖 Web 服务;WebView/浏览器在服务未运行
    /// (或尚未解析出 Web 地址)时回退为打开主界面。
    /// </summary>
    private void OpenByAction(WebOpenAction action)
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
            AppLogService.Write($"[启动] 服务未运行,{action} 回退为打开主界面");
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

        // 终止单实例管道监听(显式退出路径;ShutdownRequested 路径也已取消,此处幂等)
        _showMainWindowPipeCts?.Cancel();

        _tray?.Dispose();
        _tray = null;

        (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }
}
