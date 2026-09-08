using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace DSH_Launcher.Services
{
    /// <summary>
    /// 系统托盘服务,基于 Avalonia 内置 TrayIcon + NativeMenu。
    /// Avalonia 的 TrayIcon 只提供 Clicked 事件(每次左键点击触发),
    /// 单击/双击区分通过计时器模拟:第一次点击启动双击时长定时器,
    /// 时限内第二次点击判为双击,超时判为单击(与 WinUI 版行为一致)。
    /// 需在 UI 线程创建与使用。
    /// </summary>
    internal sealed class TrayService : IDisposable
    {
        private TrayIcon? _trayIcon;
        private DispatcherTimer? _singleClickTimer;
        private int _lastDoubleClickTickCount = Environment.TickCount;

        public event Action? SingleClickRequested;
        public event Action? DoubleClickRequested;

        /// <summary>托盘右键菜单“打开界面”。</summary>
        public event Action? MainWindowRequested;

        /// <summary>托盘右键菜单“WebView中打开”。</summary>
        public event Action? WebViewOpenRequested;

        /// <summary>托盘右键菜单“浏览器中打开”。</summary>
        public event Action? BrowserOpenRequested;

        public event Action? ExitRequested;

        /// <summary>
        /// 是否启用双击检测(单击延迟判定)。
        /// 双击行为为“无动作”时应置为 false:单击立即触发,不再等待双击间隔。
        /// 仅 UI 线程读写。
        /// </summary>
        public bool EnableDoubleClickDetection { get; set; } = true;

        private TrayService(string tip)
        {
            _trayIcon = new TrayIcon
            {
                Icon = LoadIcon(),
                ToolTipText = tip,
                IsVisible = true,
                Menu = BuildMenu(),
            };

            _trayIcon.Clicked += OnTrayClicked;

            // 把图标挂到 Application 上(必须由 Application 持有才会显示)
            if (Application.Current is not null)
            {
                TrayIcon.SetIcons(Application.Current, new TrayIcons { _trayIcon });
            }
        }

        /// <summary>创建托盘图标;失败时返回 null(不影响主功能)。</summary>
        public static TrayService? TryCreate(string tip)
        {
            try
            {
                return new TrayService(tip);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private NativeMenu BuildMenu()
        {
            var menu = new NativeMenu();
            menu.Items.Add(CreateItem("打开界面", () => MainWindowRequested?.Invoke()));
            menu.Items.Add(CreateItem("WebView中打开", () => WebViewOpenRequested?.Invoke()));
            menu.Items.Add(CreateItem("浏览器中打开", () => BrowserOpenRequested?.Invoke()));
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(CreateItem("退出程序", () => ExitRequested?.Invoke()));
            return menu;
        }

        private static NativeMenuItem CreateItem(string header, Action onClick)
        {
            var item = new NativeMenuItem(header);
            item.Click += (_, _) => onClick();
            return item;
        }

        /// <summary>
        /// 处理托盘左键点击:启用双击检测时,第一次点击挂起等待,
        /// 双击时长内的第二次点击触发双击事件,超时则触发单击事件。
        /// </summary>
        private void OnTrayClicked(object? sender, EventArgs e)
        {
            if (!EnableDoubleClickDetection)
            {
                SingleClickRequested?.Invoke();
                return;
            }

            if (_singleClickTimer is not null)
            {
                // 双击时长内第二次点击:取消挂起的单击,触发双击
                _singleClickTimer.Stop();
                _singleClickTimer = null;
                _lastDoubleClickTickCount = Environment.TickCount;
                DoubleClickRequested?.Invoke();
                return;
            }

            // 双击序列结束后紧跟的第三次点击不应被误判(防御性窗口)
            if (unchecked(Environment.TickCount - _lastDoubleClickTickCount) < GetDoubleClickTime())
            {
                return;
            }

            var timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(GetDoubleClickTime()),
            };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (ReferenceEquals(_singleClickTimer, timer))
                {
                    _singleClickTimer = null;
                    SingleClickRequested?.Invoke();
                }
            };
            _singleClickTimer = timer;
            timer.Start();
        }

        private static WindowIcon? LoadIcon()
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

        public void Dispose()
        {
            if (_singleClickTimer is not null)
            {
                _singleClickTimer.Stop();
                _singleClickTimer = null;
            }

            if (_trayIcon is not null)
            {
                _trayIcon.Clicked -= OnTrayClicked;
                if (Application.Current is not null)
                {
                    TrayIcon.SetIcons(Application.Current, new TrayIcons());
                }

                _trayIcon.Dispose();
                _trayIcon = null;
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetDoubleClickTime();
    }
}
