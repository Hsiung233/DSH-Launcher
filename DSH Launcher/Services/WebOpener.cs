using System;
using System.Diagnostics;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace DSH_Launcher.Services
{
    /// <summary>在 WebView 窗口或系统浏览器中打开 Web 端地址(供首页按钮与托盘共用)。</summary>
    public static class WebOpener
    {
        /// <summary>
        /// WebView 窗口会话(单例复用)。窗口在首次打开时创建,之后一直保留:
        /// 用户点关闭只是隐藏窗口,从而避免每次打开都重建 WebView2 环境。
        /// </summary>
        private sealed class WebViewSession
        {
            public required Window Window { get; init; }
            public required NativeWebView WebView { get; init; }
            public required WindowStateTracker Tracker { get; init; }

            /// <summary>
            /// 最近一次实际请求加载的地址。判断是否需要重新导航时必须用这个**自己记录**的值,
            /// 而不能用 <c>WebView.Source</c>:Source 会被页内跳转(SPA 路由)、以及隐藏时的
            /// 适配器处理改写,拿它比较会导致每次从托盘打开都白白重载一遍页面。
            /// </summary>
            public string RequestedUrl { get; set; } = string.Empty;
        }

        private static WebViewSession? _session;

        /// <summary>true 表示应用正在退出:此时不再拦截关闭请求,让窗口真正销毁。</summary>
        private static bool _shuttingDown;

        /// <summary>
        /// 从设置解析“隐藏后保留窗口”的时长。未设置(0)或超出范围时回退到默认 5 分钟。
        /// 实测一个已加载页面的 WebView2 子进程合计约 470MB,因此超时后要真正关闭窗口把内存还回去。
        /// 每次使用都现算,改设置无需重启即可生效。
        /// </summary>
        private static TimeSpan GetIdleCloseDelay()
        {
            var minutes = SettingsService.Instance.Settings.WebViewIdleTimeoutMinutes;
            if (minutes is < AppSettings.MinWebViewIdleTimeoutMinutes or > AppSettings.MaxWebViewIdleTimeoutMinutes)
            {
                minutes = AppSettings.DefaultWebViewIdleTimeoutMinutes;
            }

            return TimeSpan.FromMinutes(minutes);
        }

        private static DispatcherTimer? _idleCloseTimer;

        /// <summary>
        /// 应用退出前调用,放行 WebView 窗口的真正关闭。
        /// 不做这一步的话,“关闭即隐藏”的拦截会让 Shutdown() 关不掉窗口,应用无法退出。
        /// </summary>
        public static void BeginShutdown()
        {
            _shuttingDown = true;
            _idleCloseTimer?.Stop();
        }

        /// <summary>窗口转入隐藏后开始计时;超时仍未重新打开,就真正关闭窗口以释放 WebView2 内存。</summary>
        private static void ScheduleIdleClose()
        {
            if (_idleCloseTimer is null)
            {
                var timer = new DispatcherTimer();
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    CloseIfStillHidden();
                };
                _idleCloseTimer = timer;
            }

            // 每次都从设置重取时长:用户改了设置无需重启即可生效
            _idleCloseTimer.Interval = GetIdleCloseDelay();
            _idleCloseTimer.Stop();
            _idleCloseTimer.Start();
        }

        /// <summary>空闲超时回调:窗口仍处于隐藏状态时真正关闭它(释放 WebView2 进程)。</summary>
        private static void CloseIfStillHidden()
        {
            var session = _session;
            if (session is null || session.Window.IsVisible || _shuttingDown)
            {
                return;
            }

            AppLogService.Write(
                $"[WebView] 隐藏已超过 {GetIdleCloseDelay().TotalMinutes:0} 分钟,关闭窗口以释放 WebView2 内存");
            CloseSession();
        }

        /// <summary>
        /// 立即真正关闭 WebView 窗口并释放 WebView2 进程(与“关闭即隐藏”相反)。
        /// 用于 dsh 已停止(窗口里只剩死页面)、应用退出或空闲超时等场景。可在任意线程调用。
        /// </summary>
        public static void CloseSession()
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(CloseSession);
                return;
            }

            _idleCloseTimer?.Stop();

            var session = _session;
            if (session is null)
            {
                return;
            }

            // 先摘除会话:否则 Closing 里的“关闭即隐藏”会拦住这次真正的关闭
            _session = null;
            try
            {
                // 关闭前落盘位置/尺寸/最大化状态,保证下次打开仍能还原
                session.Tracker.Save();
                session.Window.Close();
                AppLogService.Write("[WebView] 已关闭窗口并释放 WebView2 进程");
            }
            catch (Exception ex)
            {
                AppLogService.Write($"[WebView] 关闭窗口失败: {ex.Message}");
            }
        }

        /// <summary>按指定方式打开地址。</summary>
        public static void Open(WebOpenAction action, string url)
        {
            if (action == WebOpenAction.WebView)
            {
                OpenInWebView(url);
            }
            else
            {
                OpenInBrowser(url);
            }
        }

        /// <summary>在系统默认浏览器中打开。</summary>
        public static void OpenInBrowser(string url)
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }

        /// <summary>
        /// 处理 WebView 内部发起的“新窗口/新标签”请求(target="_blank"、window.open 等)。
        /// 由设置项 <see cref="WebViewLinkTarget"/> 决定去向:系统浏览器 / 应用内 WebView 窗口 / 不接管。
        /// </summary>
        private static void OnNewWindowRequested(WebViewNewWindowRequestedEventArgs e)
        {
            var mode = SettingsService.Instance.Settings.WebViewLink;
            if (mode == WebViewLinkTarget.Unhandled)
            {
                // 不置 Handled,完全交给 WebView2 底层行为
                return;
            }

            var target = e.Request;
            if (target is null)
            {
                return;
            }

            // 接管:必须置 Handled,否则底层可能又自行开一个窗口。
            // 注意:e 只在本次回调内有效,之后(尤其跨线程)不能再捕获它。
            e.Handled = true;

            var url = target.ToString();
            AppLogService.Write($"[WebView] 页面请求打开链接({mode}): {url}");

            if (mode == WebViewLinkTarget.SystemBrowser)
            {
                OpenInBrowser(url);
                return;
            }

            // 在当前 WebView 窗口里加载。target 是我们自己持有的 Uri,跨线程捕获是安全的。
            if (Dispatcher.UIThread.CheckAccess())
            {
                NavigateInSession(target);
            }
            else
            {
                Dispatcher.UIThread.Post(() => NavigateInSession(target));
            }
        }

        /// <summary>在应用内 WebView 会话窗口里加载地址(需在 UI 线程调用);无会话时退回系统浏览器。</summary>
        private static void NavigateInSession(Uri target)
        {
            var session = _session;
            if (session is null)
            {
                OpenInBrowser(target.ToString());
                return;
            }

            // 记录新地址:之后按 dsh 地址打开时,因地址不同会正确地导航回 dsh 页面
            session.RequestedUrl = target.ToString();
            session.WebView.Navigate(target);
        }

        /// <summary>
        /// 在应用内 WebView 窗口中打开。已有窗口时只把窗口切到前台,**不重载页面**
        /// (窗口关闭只是隐藏,页面与 WebView2 进程都保留着)。
        /// 仅当 dsh 地址确实变化(dsh 重启后 token 变了、或改了监听端口)才重新导航。
        /// WebView2 运行时不可用等异常时退回系统浏览器。
        /// </summary>
        public static void OpenInWebView(string url)
        {
            // 托盘回调可能来自非 UI 线程,而本方法会创建/操作控件;统一调度到 UI 线程
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(() => OpenInWebView(url));
                return;
            }

            var existing = _session;
            if (existing is not null)
            {
                // 复用已有窗口:直接切到前台,不重载页面(页面和 WebView2 进程都还在)。
                // 只有 dsh 地址真的变了(dsh 重启后 token 变化、或改了端口)才需要重新导航,
                // 否则会把用户当前所在的页面状态重置掉。
                _idleCloseTimer?.Stop();

                if (existing.RequestedUrl != url)
                {
                    existing.RequestedUrl = url;
                    existing.WebView.Navigate(new Uri(url));
                    AppLogService.Write($"[WebView] dsh 地址已变化,复用窗口并导航到新地址: {url}");
                }
                else
                {
                    AppLogService.Write("[WebView] 复用已有窗口(直接显示,不重载)");
                }

                existing.Window.Show();
                existing.Window.Activate();
                return;
            }

            CreateAndShow(url);
        }

        /// <summary>首次打开:创建窗口与 WebView2 适配器(冷启动,较慢),之后由 <see cref="OpenInWebView"/> 复用。</summary>
        private static void CreateAndShow(string url)
        {
            Window? window = null;
            NativeWebView? webView = null;
            try
            {
                webView = new NativeWebView();

                webView.NavigationStarted += (_, _) =>
                {
                    AppLogService.Write($"[WebView] 开始导航");
                };
                webView.NavigationCompleted += (_, e) =>
                {
                    AppLogService.Write($"[WebView] 导航{(e.IsSuccess ? "完成" : "失败")}");
                };
                // 页面自己发起的“新窗口/新标签”请求(如 target="_blank"、window.open),
                // 按设置决定交给系统浏览器还是在应用内 WebView 打开
                webView.NewWindowRequested += (_, e) => OnNewWindowRequested(e);

                window = new Window
                {
                    Title = "DSH Web",
                    Content = webView,
                    Width = 960,
                    Height = 640,
                };

                var icon = LoadAppIcon();
                if (icon is not null)
                {
                    window.Icon = icon;
                }

                // 恢复上次的位置/大小与最大化状态;无记录或屏幕校验失败时用默认尺寸(工作区 60%×70% 居中)
                var bounds = WindowStateService.Instance.RestoreWebViewWindow(window);
                if (bounds is null)
                {
                    var screen = window.Screens?.Primary;
                    if (screen is not null)
                    {
                        var workArea = screen.WorkingArea;
                        var scaling = window.RenderScaling;
                        var widthDip = workArea.Width * 0.6 / scaling;
                        var heightDip = workArea.Height * 0.7 / scaling;
                        var x = workArea.X + (int)((workArea.Width - widthDip * scaling) / 2);
                        var y = workArea.Y + (int)((workArea.Height - heightDip * scaling) / 2);

                        window.Width = widthDip;
                        window.Height = heightDip;
                        window.Position = new PixelPoint(x, y);

                        // 作为跟踪器的初始值:即使窗口还没记录到任何几何变化就被最大化/关闭,
                        // 也能存下正确的还原尺寸
                        bounds = new WindowBounds { X = x, Y = y, Width = widthDip, Height = heightDip };
                    }
                }

                // 持续记录非最大化时的位置/尺寸;关闭前保存(含是否最大化)
                var tracker = WindowStateService.Instance.TrackWebViewWindow(window, bounds);

                window.Closing += (_, e) =>
                {
                    // 关闭前记录位置/尺寸/最大化状态(Closed 时平台实现已销毁,Position 会回退为 0,0,
                    // 存了就会导致下次打开位置重置到左上角)
                    tracker.Save();

                    // 应用正在退出,或该窗口已从会话中摘除(创建失败的回退路径):放行,让窗口真正关闭
                    if (_shuttingDown || !ReferenceEquals(_session?.Window, window))
                    {
                        return;
                    }

                    // 用户点关闭 → 按设置决定:保留(仅隐藏)还是立即释放
                    if (!SettingsService.Instance.Settings.KeepWebViewAlive)
                    {
                        // 不保留:放行关闭,WebView2 进程随之释放(下次打开需重新加载)
                        AppLogService.Write("[WebView] 按设置不保留窗口,关闭并释放 WebView2 进程");
                        return;
                    }

                    // 保留窗口与 WebView2 适配器:下次打开只是一次 Show(),不必再付冷启动的代价
                    e.Cancel = true;
                    window.Hide();
                    AppLogService.Write("[WebView] 已隐藏窗口(保留 WebView2 进程,便于快速再次打开)");
                    ScheduleIdleClose();
                };
                window.Closed += (_, _) =>
                {
                    tracker.Dispose();
                    _session = null;
                };

                _session = new WebViewSession
                {
                    Window = window,
                    WebView = webView,
                    Tracker = tracker,
                    RequestedUrl = url,
                };
                window.Show();
                window.Activate();
                AppLogService.Write("[WebView] 窗口已创建并显示");

                // Source 赋值会在适配器就绪后自动导航;适配器创建失败(如缺少 WebView2 运行时)
                // 不会同步抛异常,这里仅记录日志便于诊断。
                webView.Source = new Uri(url);
                AppLogService.Write($"[WebView] 已请求加载: {url}");
            }
            catch (Exception ex)
            {
                AppLogService.Write($"[WebView] 打开异常({ex.GetType().Name}: {ex.Message}),退回系统浏览器");

                // 先摘除会话再关闭:否则 Closing 里的“关闭即隐藏”会拦住这次回退关闭
                _session = null;
                try
                {
                    window?.Close();
                }
                catch
                {
                    // 忽略关闭异常
                }

                OpenInBrowser(url);
            }
        }

        /// <summary>保存 WebView 窗口的位置/尺寸/最大化状态(应用退出前调用,防止关机路径上 Closed 事件不可靠)。</summary>
        public static void SaveOpenWebViewBounds()
        {
            _session?.Tracker.Save();
        }

        private static WindowIcon? LoadAppIcon()
        {
            try
            {
                var pngPath = Path.Combine(AppContext.BaseDirectory, "Assets", "logo-512.png");
                if (File.Exists(pngPath))
                {
                    return new WindowIcon(new Bitmap(pngPath));
                }
            }
            catch (Exception)
            {
                // 图标加载失败不影响功能
            }

            return null;
        }
    }
}
