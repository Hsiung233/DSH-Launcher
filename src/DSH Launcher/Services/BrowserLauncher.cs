using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;

namespace DSH_Launcher.Services
{
    /// <summary>
    /// 用系统默认浏览器打开地址。
    /// <para>
    /// 为什么从 <see cref="WebOpener"/> 里拆出来:它跟 WebView 窗口毫无关系 ——
    /// 插件页打开目录条目页、WebView 里 target="_blank" 的兜底、首页的"浏览器打开"用的都是它,
    /// 而 WebView 会话(窗口复用/空闲释放)是另一件事。放在一起会让"打开浏览器"也要经过一整篇窗口管理的代码。
    /// </para>
    /// </summary>
    internal static class BrowserLauncher
    {
        /// <summary>在系统默认浏览器中打开。返回 false 表示当前平台没有可用的打开方式。</summary>
        public static bool OpenInBrowser(string url)
        {
            var uri = new Uri(url);

            // 用 Avalonia 官方启动器(TopLevel.Launcher → 平台 ILauncher 实现):
            // Windows = shell 关联程序、macOS = open、Linux = xdg-open,各自平台正确;
            // 不再手写 open/xdg-open 分支。Win32 实现即 BclLauncher(UseShellExecute=true),
            // 与旧手写行为等价。
            // 任意 TopLevel(主窗口或 WebView 窗口)都可以,托盘回调线程拿不到窗口时
            // 退回经 AvaloniaLocator 取 static TopLevel 实现,都失败则自行走 Process。
            var launcher = GetLauncher();
            if (launcher is not null)
            {
                var ok = launcher.LaunchUriAsync(uri).GetAwaiter().GetResult();
                if (ok)
                {
                    return true;
                }

                AppLogService.Write($"[浏览器] Launcher 拒绝打开({url}),退回直接启动方式");
            }

            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                return true;
            }
            catch (Exception ex)
            {
                AppLogService.Write($"[浏览器] 打开失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 取 Avalonia 官方 <see cref="ILauncher"/>。需要已创建的 TopLevel/窗口:
        /// Win32 的 WindowImpl.TryGetFeature(ILauncher) 返回 BclLauncher。
        /// 服务类没有窗口引用,这里遍历应用当前窗口取第一个;没有窗口(极早期/纯托盘回调)时返回 null。
        /// </summary>
        private static ILauncher? GetLauncher()
        {
            try
            {
                if (Application.Current?.ApplicationLifetime
                    is IClassicDesktopStyleApplicationLifetime desktop)
                {
                    foreach (var window in desktop.Windows)
                    {
                        if (window.PlatformImpl is not null)
                        {
                            return window.Launcher;
                        }
                    }
                }
            }
            catch (Exception)
            {
                // 生命周期不可用时返回 null,由调用方回退
            }

            return null;
        }
    }
}
