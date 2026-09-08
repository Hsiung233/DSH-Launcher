using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace DSH_Launcher.Services
{
    /// <summary>在 WebView 窗口或系统浏览器中打开 Web 端地址(供首页按钮与托盘共用)。</summary>
    public static class WebOpener
    {
        /// <summary>已打开的 WebView 窗口会话(单例复用),防止窗口被 GC 回收。</summary>
        private sealed class WebViewSession
        {
            public required Window Window { get; init; }
            public required NativeWebView WebView { get; init; }
        }

        private static readonly List<WebViewSession> OpenWindows = new();

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

        /// <summary>在应用内 WebView 窗口中打开;已有 WebView 窗口时直接切到该窗口。
        /// 若窗口内的地址与请求不一致(如 dsh 重启后 token 已变),则导航到新地址。
        /// WebView2 运行时不可用等异常时退回系统浏览器。</summary>
        public static void OpenInWebView(string url)
        {
            if (OpenWindows.Count > 0)
            {
                // 复用已有窗口:置前;地址过期(如 token 变化)则重新导航
                var existing = OpenWindows[0];
                if (existing.WebView.Source?.ToString() != url)
                {
                    existing.WebView.Navigate(new Uri(url));
                    AppLogService.Write($"[WebView] 复用已有窗口,导航到新地址: {url}");
                }

                existing.Window.Show();
                existing.Window.Activate();
                return;
            }

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

                // 恢复上次的位置/大小;无记录或屏幕校验失败时用默认尺寸(工作区 60%×70% 居中)
                if (!WindowStateService.Instance.RestoreWebViewWindow(window))
                {
                    var screen = window.Screens?.Primary;
                    if (screen is not null)
                    {
                        var workArea = screen.WorkingArea;
                        var scaling = window.RenderScaling;
                        var widthDip = workArea.Width * 0.6 / scaling;
                        var heightDip = workArea.Height * 0.7 / scaling;
                        window.Width = widthDip;
                        window.Height = heightDip;
                        window.Position = new PixelPoint(
                            workArea.X + (int)((workArea.Width - widthDip * scaling) / 2),
                            workArea.Y + (int)((workArea.Height - heightDip * scaling) / 2));
                    }
                }

                window.Closing += (_, _) =>
                {
                    // 关闭前记录位置/尺寸(Closed 时平台实现已销毁,Position 会回退为 0,0,
                    // 存了就会导致下次打开位置重置到左上角)
                    WindowStateService.Instance.SaveWebViewWindow(window);
                };
                window.Closed += (_, _) =>
                {
                    OpenWindows.Clear();
                };

                OpenWindows.Add(new WebViewSession { Window = window, WebView = webView });
                window.Show();
                window.Activate();

                // Source 赋值会在适配器就绪后自动导航;适配器创建失败(如缺少 WebView2 运行时)
                // 不会同步抛异常,这里仅记录日志便于诊断。
                webView.Source = new Uri(url);
                AppLogService.Write($"[WebView] 已请求加载: {url}");
            }
            catch (Exception ex)
            {
                AppLogService.Write($"[WebView] 打开异常({ex.GetType().Name}: {ex.Message}),退回系统浏览器");
                try
                {
                    window?.Close();
                }
                catch
                {
                    // 忽略关闭异常
                }
                OpenWindows.Clear();
                OpenInBrowser(url);
            }
        }

        /// <summary>保存所有已打开 WebView 窗口的 bounds(应用退出前调用,防止关机路径上 Closed 事件不可靠)。</summary>
        public static void SaveOpenWebViewBounds()
        {
            foreach (var session in OpenWindows)
            {
                WindowStateService.Instance.SaveWebViewWindow(session.Window);
            }
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
