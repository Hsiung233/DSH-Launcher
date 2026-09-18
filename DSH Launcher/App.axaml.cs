using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
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

    // 单实例协调:互斥体判定首个实例,命名管道通知已有实例显示主界面。
    // 用命名管道而非命名 EventWaitHandle:后者仅 Windows 支持,macOS/Linux 上创建即抛
    // PlatformNotSupportedException;命名管道全平台可用(Unix 上映射为 Unix 域套接字)。
    private const string SingleInstanceMutexName = "DSH_Launcher_SingleInstance";
    private const string ShowMainWindowPipeName = "DSH_Launcher_ShowMainWindow";
    private Mutex? _singleInstanceMutex;
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

            // 单实例:已有实例在运行时,通过命名管道通知其显示主界面,然后退出当前进程
            _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out var createdNew);
            if (!createdNew && !TryAcquireSingleInstanceMutex())
            {
                try
                {
                    // 连接上即视为"显示主界面"信号,无需写入数据
                    using var client = new NamedPipeClientStream(".", ShowMainWindowPipeName, PipeDirection.Out);
                    client.Connect(1000);
                    AppLogService.Write("[启动] 检测到已有实例,已通知其显示主界面");
                }
                catch (Exception)
                {
                    // 连接失败(已有实例可能正在退出)时直接退出,不影响已有实例
                }

                Environment.Exit(0);
                return;
            }

            // 上游进程链逃逸(2026-09-18 端到端实验定位):凡是被别的进程当子进程拉起的实例
            // (安装器的"安装完成后启动"是典型来源,第三方/企业部署工具、将来可能的自动更新器同理),
            // 无论环境变量怎么清理、cwd/令牌如何,dsh 服务启动必败(整片 Cannot find package),
            // 而同一 exe 手动启动必成 —— Windows 在加载器层随**进程链**继承的 AppCompat shim 才是元凶,
            // 清环境变量拦不住。唯一可靠解法:由 explorer.exe(干净链根)重新拉起自己,自己退出。
            // ⚠ 定位:**独立于安装器的通用自愈**,不是 installer.iss 那条 [Run] 的补丁。
            //   installer.iss 改走 explorer 修的是"正常路径不再产生这种链",本条管的是"任何链都能自愈" ——
            //   不要因为安装器修好了就判定它是死代码而删除(别的部署方式照样会把本程序放进自己的进程链)。
            // 探测后旧实例先释放互斥体再退出,避免新实例走"通知已有实例"路径被误退。
            // 循环护栏:逃逸前写标记文件;若本实例是刚被逃逸拉起的(标记还在),不再逃逸 ——
            // 覆盖 __COMPAT_LAYER 被持久化的极端情况(否则 explorer 拉起的新实例照样非空 → 无限重启)。
            if (IsInsideInheritedCompatChain())
            {
                if (!ConsumeEscapeMarker())
                {
                    if (TryEscapeInheritedChain())
                    {
                        Environment.Exit(0);
                        return;
                    }
                    // 逃逸失败:继续以当前实例运行(保底旧行为),不退出
                }
            }
            else
            {
                // 本次是干净链:上一次逃逸留下的标记已经无意义,顺手清掉 ——
                // 否则它会让 2 分钟内"下一次带链启动"误判为"我就是逃逸拉起的"而跳过逃逸,直接回到必败状态
                TryDeleteEscapeMarker();
            }

            _showMainWindowPipeCts = new CancellationTokenSource();
            _ = Task.Run(() => ListenForShowMainWindowRequestsAsync(_showMainWindowPipeCts.Token));

            // 记录应用启动标记到文件日志(%LOCALAPPDATA%\DSH Launcher\Settings\app.log)
            AppLogService.MarkSessionStart();

            // 启动时把"兼容层/提权"相关的环境证据写进 app.log,便于事后比对两条启动路径的差异
            AppLogService.Write(ChildEnvironment.DescribeInheritedVariables());

            // 提权信息已由上面那行 app.log(DescribeInheritedVariables)留证,不再往面板里提示:
            // 实测提权与兼容层变量本身都不是 dsh 失败的原因(真因是上游进程链的链级兼容层标记;
            // 正解见本文件顶部的“上游进程链逃逸”逻辑)。

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

    /// <summary>
    /// 后台监听"显示主界面"请求(来自二次启动的进程,经命名管道)。
    /// 命名管道服务端一次只能服务一个连接,每接受一个连接就重建一次监听;
    /// 连接本身即信号,无需读取数据。应用退出时由取消标记终止监听。
    /// </summary>
    private async Task ListenForShowMainWindowRequestsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    ShowMainWindowPipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(cancellationToken);
                Avalonia.Threading.Dispatcher.UIThread.Post(ShowMainWindow);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // 监听异常不致命:稍后重建管道重试;退避一下,避免高频失败刷满 CPU
                AppLogService.Write($"[启动] 单实例管道监听异常: {ex.Message}");
                try
                {
                    await Task.Delay(1000, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
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

    /// <summary>
    /// 当前进程是否处于“上游进程链”里(以 __COMPAT_LAYER 非空为信号),即被别的进程当作子进程拉起。
    /// <para>
    /// 判定依据(2026-09-18 端到端实验):安装器“安装完成后启动”拉起的实例,环境里必有非空
    /// <c>__COMPAT_LAYER</c>(实测见过 <c>DetectorsAppHealth</c>/<c>ElevateCreateProcess</c> 两种,
    /// 随上游进程本身被哪个进程拉起而异),而手动/explorer 启动永远为空;此类实例的 dsh 服务启动必败
    /// (链级标记,清环境变量无效),所以一律经 explorer.exe 逃逸重启。普通用户不会手动给本程序设
    /// <c>__COMPAT_LAYER</c>(右键兼容性设置写入的是 HKCU\...\AppCompatFlags\Layers,不走环境变量)。
    /// </para>
    /// <para>
    /// ⚠ 判定条件**只看“非空”,不要按值收窄**(不能只认 <c>ElevateCreateProcess</c> 之类):
    /// 实测链上的值随上游进程变化,按值匹配会把已修复的场景重新放进坑里。误判(如被写成持久环境变量)
    /// 的代价只是“多重启一次”,已由 <see cref="ConsumeEscapeMarker"/> 的护栏与提示兑付。
    /// </para>
    /// </summary>
    private static bool IsInsideInheritedCompatChain()
    {
        try
        {
            var layer = Environment.GetEnvironmentVariable("__COMPAT_LAYER");
            return !string.IsNullOrWhiteSpace(layer);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>逃逸标记文件路径(与 app.log 同目录)。文件名沿用历史命名,作为稳定标识保持不改。</summary>
    private static string EscapeMarkerPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DSH Launcher", "Settings", "installer-escape.marker");

    /// <summary>
    /// 若本实例是刚被逃逸拉起的(标记文件存在且新鲜),吃掉标记并返回 true(=不再逃逸,直接继续运行)。
    /// <para>
    /// 这是循环护栏:<c>__COMPAT_LAYER</c> 若被持久化(用户/软件写成 HKCU\Environment 变量),
    /// explorer 拉起的新实例照样非空 —— 没有标记就会无限重启。标记只在逃逸前写入、
    /// 被拉起的实例首次启动时消费一次;标记过期(超过 1 分钟)视为陈旧残留,照常允许逃逸。
    /// </para>
    /// </summary>
    private bool ConsumeEscapeMarker()
    {
        try
        {
            var path = EscapeMarkerPath;
            if (!File.Exists(path))
            {
                return false;
            }

            var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(path);
            File.Delete(path);
            if (age <= TimeSpan.FromMinutes(2))
            {
                AppLogService.Write("[启动] 本实例是刚由进程链逃逸拉起的(逃逸标记有效),不再二次逃逸,直接继续运行。");

                // 护栏触发说明:逃逸过一次却仍带着链级标记 ⇒ __COMPAT_LAYER 很可能是**持久**环境变量,
                // 此时 explorer 拉起的新实例同样被套层、dsh 仍会失败。只在这里往面板说一句,
                // 免得用户看到"启动失败"却不知道去哪儿查。
                DshService.Instance.AppendSystemLog(
                    "环境提示:检测到链级兼容层标记,且本实例已经是逃逸后重开的,因此不再重启。"
                    + "若 dsh 启动失败,请检查是否有软件把 __COMPAT_LAYER 写成了持久环境变量"
                    + "(用户/系统环境变量)并清除它。");
                return true;
            }

            AppLogService.Write("[启动] 发现陈旧的逃逸标记(已超过 2 分钟),忽略并按新逃逸处理。");
            return false;
        }
        catch (Exception)
        {
            // 标记读写失败时按"无标记"处理:宁可逃逸一次,也不冒无限重启的风险
            return false;
        }
    }

    /// <summary>
    /// 由 explorer.exe 重新拉起自己以逃逸上游进程链(任何把本程序当子进程拉起的父进程)。
    /// <para>
    /// 返回 true 表示逃逸重启已发起(调用方应退出);false 表示失败(调用方应继续运行,保底旧行为)。
    /// 必须先释放单实例互斥体再拉起:explorer 创建新进程需要时间,若旧实例仍持有互斥体,
    /// 新实例会走"检测到已有实例"路径被误退。explorer 自己是干净的加载器链根,
    /// 由它创建的新实例不继承任何链标记,环境也换成用户会话环境(无兼容层变量)。
    /// </para>
    /// <para>
    /// 已知取舍(有意为之,不要当 bug 修):重启**不带命令行参数**,explorer 只把第一个参数当“要打开的对象”。
    /// 目前应用不解析参数(<c>Program.cs</c> 仅把 args 转交给 Avalonia 生命周期),所以无实际影响;
    /// 若将来需要靠参数驱动启动行为,必须同步改这里(例如改为经临时快捷方式/自己拼 ShellExecuteEx 传递)。
    /// 工作目录会变成 exe 所在目录 —— 与“手动双击启动”一致,比继承上游 cwd 更可预测。
    /// </para>
    /// </summary>
    private bool TryEscapeInheritedChain()
    {
        var exe = Environment.ProcessPath;
        AppLogService.Write("[启动] 检测到上游进程链带来的兼容层标记(__COMPAT_LAYER 非空):将由 explorer.exe 以干净环境重新拉起本程序并退出当前实例。");

        // 先释放互斥体,新实例才能正常取得单实例所有权
        try
        {
            _singleInstanceMutex?.ReleaseMutex();
        }
        catch (Exception)
        {
            // 释放失败也不阻断逃逸(极端情况下新实例会走通知路径退出,用户手动启动即可恢复)
        }

        try
        {
            _singleInstanceMutex?.Dispose();
        }
        catch (Exception)
        {
            // 忽略
        }

        _singleInstanceMutex = null;

        try
        {
            if (string.IsNullOrEmpty(exe))
            {
                AppLogService.Write("[启动] 无法确定自身可执行文件路径,放弃逃逸,继续以当前实例运行。");
                return false;
            }

            // 写逃逸标记(给拉起的新实例看,见 ConsumeEscapeMarker)
            Directory.CreateDirectory(Path.GetDirectoryName(EscapeMarkerPath)!);
            File.WriteAllText(EscapeMarkerPath, DateTime.UtcNow.ToString("O"));

            // explorer.exe 会把参数当作要打开的对象,对 exe 即是"启动该程序",
            // 新进程的父进程是 explorer(不在上游进程链里)。
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{exe}\"") { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            // 逃逸失败不阻断当前实例:继续运行,启动 dsh 时最多重试失败(旧行为)。
            // 但必须收尾两件事,否则"逃逸失败"会留下比原状更糟的状态:
            // ① 清掉刚写的标记 —— 否则 2 分钟内的下一次启动会误判"我是逃逸拉起的"而跳过逃逸;
            // ② 把单实例互斥体重新拿回来 —— 上面已经 Release/Dispose 过了,不拿回来本实例就成了"没有锁的实例",
            //    之后任何一次启动都会成功创建第二个实例(两个托盘图标、各自都能起 dsh)。
            AppLogService.Write($"[启动] 逃逸重启失败,继续以当前实例运行:{ex.Message}");
            TryDeleteEscapeMarker();
            TryReacquireSingleInstanceMutex();
            return false;
        }
    }

    /// <summary>删除逃逸标记(不存在或删除失败都不影响流程)。</summary>
    private static void TryDeleteEscapeMarker()
    {
        try
        {
            var path = EscapeMarkerPath;
            if (File.Exists(path))
            {
                File.Delete(path);
                AppLogService.Write("[启动] 已清理逃逸标记。");
            }
        }
        catch (Exception)
        {
            // 标记清理失败不影响流程(它 2 分钟后会自动失效)
        }
    }

    /// <summary>
    /// 逃逸失败后的收尾:把单实例互斥体重新拿回来,避免本实例变成"没有锁的实例"。
    /// 拿不回来只记日志(极端情况下用户会看到两个实例,退出一个即可)。
    /// </summary>
    private void TryReacquireSingleInstanceMutex()
    {
        try
        {
            _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out var createdNew);
            AppLogService.Write(createdNew
                ? "[启动] 已重新取得单实例所有权。"
                : "[启动] 单实例互斥体已被别的实例占用(本次未取回)。");
        }
        catch (Exception ex)
        {
            AppLogService.Write($"[启动] 重新取得单实例所有权失败:{ex.Message}");
        }
    }

    /// <summary>
    /// 在"已有实例正在退出"的短暂窗口里再争取一次单实例所有权。
    /// <para>
    /// 为什么需要:进程链逃逸时旧实例要先 <c>ReleaseMutex</c> 再让 explorer 拉起新实例,
    /// 两个动作之间有几百毫秒;若这期间有别的实例抢先拿到互斥体,逃逸出来的实例会走
    /// "通知已有实例并退出"这条路 —— 用户看到的就是"点了没反应"。
    /// 命中被遗弃的互斥体(<c>AbandonedMutexException</c>)算作"已获得",等于接管它。
    /// </para>
    /// </summary>
    private bool TryAcquireSingleInstanceMutex()
    {
        for (var i = 0; i < 6; i++)
        {
            Thread.Sleep(250);
            try
            {
                if (_singleInstanceMutex?.WaitOne(0) == true)
                {
                    AppLogService.Write($"[启动] 等待 {(i + 1) * 250} 毫秒后取得单实例所有权(上一个实例正在退出)");
                    return true;
                }
            }
            catch (AbandonedMutexException)
            {
                // 上一个实例没来得及释放就退出了:这个异常本身表示所有权已归本进程
                AppLogService.Write("[启动] 接管了被遗弃的单实例互斥体");
                return true;
            }
            catch (Exception)
            {
                // 其它异常按"未取得"继续重试
            }
        }

        return false;
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
