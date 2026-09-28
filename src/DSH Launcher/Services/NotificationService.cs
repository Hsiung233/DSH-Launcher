using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;

namespace DSH_Launcher.Services
{
    /// <summary>通知的严重级别(映射到 Windows 通知的系统图标)。</summary>
    public enum NotificationKind
    {
        /// <summary>普通信息。</summary>
        Info = 0,

        /// <summary>警告(如服务意外退出)。</summary>
        Warning = 1,

        /// <summary>错误(如启动失败)。</summary>
        Error = 2,
    }

    /// <summary>
    /// 系统通知(Win32 Shell_NotifyIcon 气泡,Win10/11 上由系统渲染成 Toast)。
    /// <para>
    /// 实现方式:一条专用后台线程持有一个**隐藏的**托盘图标(NIS_HIDDEN —— 图标不显示在通知区,
    /// 但仍可发送气泡通知,这是文档明确支持的行为),所有 <see cref="Show"/> 调用经消息队列
    /// 汇到该线程执行。选这条路线而不是 WinRT Toast API 的理由:
    /// </para>
    /// <list type="bullet">
    /// <item>WinRT Toast 对非打包应用要求 AUMID + 开始菜单快捷方式配合,开发运行(dotnet run)
    /// 与绿色目录场景下会静默失败;Shell_NotifyIcon 气泡对这些场景一视同仁。</item>
    /// <item>不引入任何新依赖(H.NotifyIcon 等库会连托盘图标一起接管,与现有 <see cref="TrayService"/> 冲突)。</item>
    /// </list>
    /// <para>
    /// 防御性约定:**任何失败都不抛异常、不影响主功能**(通知区被组策略禁用、Explorer 正在重启、
    /// 图标加载失败等一律退化为"只写 app.log")。线程安全:可从任意线程调用。
    /// 非 Windows 平台是空实现(只写日志),调用方无须判断平台。
    /// </para>
    /// </summary>
    public static partial class NotificationService
    {
        /// <summary>气泡正文的长度上限(szInfo 缓冲区 256 字符,含结尾留量)。</summary>
        private const int MaxMessageChars = 250;

        /// <summary>气泡标题的长度上限(szInfoTitle 缓冲区 64 字符,含结尾留量)。</summary>
        private const int MaxTitleChars = 60;

        /// <summary>Worker 线程处理的通知负载队列(Show 生产,WndProc 消费)。</summary>
        private static readonly ConcurrentQueue<NotificationItem> Pending = new();

        private static readonly object StartLock = new();
        private static bool _started;
        private static IntPtr _window;
        private static uint _callbackMessage;
        private static bool _iconRegistered;

        /// <summary>
        /// WndProc 委托必须持有引用:RegisterClass 只拿到函数指针,
        /// 委托实例被 GC 回收后回调就成了野指针(经典崩溃源)。
        /// </summary>
        private static readonly WndProcDelegate WndProcCallback = WndProc;

        public static void Show(string title, string message, NotificationKind kind = NotificationKind.Info)
        {
            title = Truncate(title, MaxTitleChars);
            message = Truncate(message, MaxMessageChars);

            // 非 Windows:启动器的主要交付平台是 Windows,其他平台退化为只写日志
            if (!OperatingSystem.IsWindows())
            {
                AppLogService.Write($"[通知] ({kind}) {title}: {message}");
                return;
            }

            AppLogService.Write($"[通知] ({kind}) {title}: {message}");
            EnsureWorkerStarted();
            if (_window == IntPtr.Zero)
            {
                // Worker 启动失败(极端环境):通知没有落脚点,日志里已有内容,直接返回
                return;
            }

            Pending.Enqueue(new NotificationItem(title, message, kind));
            PostMessage(_window, _callbackMessage, IntPtr.Zero, IntPtr.Zero);
        }

        private static void EnsureWorkerStarted()
        {
            lock (StartLock)
            {
                if (_started)
                {
                    return;
                }

                _started = true;
                var thread = new Thread(WorkerMain)
                {
                    IsBackground = true,
                    Name = "DSH Launcher Notifications",
                };
                // 调用方已保证仅 Windows 走到这里(Show 里的 OperatingSystem.IsWindows 分支)
                if (OperatingSystem.IsWindows())
                {
                    thread.SetApartmentState(ApartmentState.STA);
                }

                thread.Start();
            }
        }

        /// <summary>
        /// Worker 线程主循环:注册窗口类 → 建隐藏窗口 → 注册隐藏托盘图标 → 消息循环。
        /// 所有 Shell_NotifyIcon 调用都发生在这条线程上(它要求与注册时同一 STA 线程)。
        /// </summary>
        private static void WorkerMain()
        {
            try
            {
                if (!TryCreateHiddenWindow(out _window, out _callbackMessage))
                {
                    return;
                }

                TryRegisterHiddenIcon();

                while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
                {
                    TranslateMessage(ref message);
                    DispatchMessage(ref message);
                }
            }
            catch (Exception ex)
            {
                AppLogService.Write($"[通知] 通知线程异常退出: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void TryRegisterHiddenIcon()
        {
            var data = CreateBaseData();
            data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_STATE;
            data.dwState = NIS_HIDDEN;      // 隐藏图标:不占通知区位置,但仍能弹气泡
            data.dwStateMask = NIS_HIDDEN;
            data.hIcon = LoadNotificationIcon();

            _iconRegistered = Shell_NotifyIcon(NIM_ADD, ref data);
            if (!_iconRegistered)
            {
                // Explorer 未就绪/通知区被禁用等:不重试阻塞,Show 时会再试
                AppLogService.Write("[通知] 注册通知图标失败(系统通知可能不可用)");
            }
        }

        private static NOTIFYICONDATAW CreateBaseData()
        {
            return new NOTIFYICONDATAW
            {
                cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
                hWnd = _window,
                uID = 1,
                uCallbackMessage = _callbackMessage,
            };
        }

        /// <summary>WndProc:自定义回调消息到来时把队列里的通知逐个发出去。</summary>
        private static IntPtr WndProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam)
        {
            if (message == _callbackMessage)
            {
                DrainQueue();
            }

            return DefWindowProc(hWnd, message, wParam, lParam);
        }

        private static void DrainQueue()
        {
            while (Pending.TryDequeue(out var item))
            {
                if (!_iconRegistered && !TryRegisterHiddenIconNow())
                {
                    // 注册不了就丢这条:通知是尽力而为的服务,不能为它排队阻塞
                    continue;
                }

                var data = CreateBaseData();
                data.uFlags = NIF_INFO;
                data.szInfoTitle = item.Title;
                data.szInfo = item.Message;
                data.dwInfoFlags = item.Kind switch
                {
                    NotificationKind.Warning => NIIF_WARNING,
                    NotificationKind.Error => NIIF_ERROR,
                    _ => NIIF_NONE,
                };

                if (!Shell_NotifyIcon(NIM_MODIFY, ref data))
                {
                    // 图标可能在 Explorer 重启后失效:标记未注册,下一条通知时重新注册
                    _iconRegistered = false;
                    AppLogService.Write("[通知] 发送系统通知失败(可能 Explorer 正在重启)");
                }
            }
        }

        private static bool TryRegisterHiddenIconNow()
        {
            var data = CreateBaseData();
            data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_STATE;
            data.dwState = NIS_HIDDEN;
            data.dwStateMask = NIS_HIDDEN;
            data.hIcon = LoadNotificationIcon();

            _iconRegistered = Shell_NotifyIcon(NIM_ADD, ref data);
            return _iconRegistered;
        }

        /// <summary>通知用的图标:从 exe 自身提取(编译期已由 ApplicationIcon 嵌入 logo.ico)。</summary>
        private static IntPtr LoadNotificationIcon()
        {
            try
            {
                var exePath = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exePath)
                    && ExtractIconEx(exePath, 0, out var large, out var small, 1) > 0)
                {
                    // 气泡用不到大图标,用完即毁,只留 16/32 都可的那份
                    var icon = large != IntPtr.Zero ? large : small;
                    if (large != IntPtr.Zero && small != IntPtr.Zero)
                    {
                        DestroyIcon(small);
                    }

                    return icon;
                }
            }
            catch (Exception)
            {
                // 图标提取失败不影响通知本身
            }

            // 系统默认应用图标兜底
            return LoadIcon(IntPtr.Zero, IDI_APPLICATION);
        }

        private static string Truncate(string value, int maxLength)
        {
            value = value ?? string.Empty;
            return value.Length <= maxLength ? value : value[..maxLength] + "…";
        }

        private readonly record struct NotificationItem(string Title, string Message, NotificationKind Kind);

        // ---- Win32 ----

        private const uint NIM_ADD = 0;
        private const uint NIM_MODIFY = 1;
        private const uint NIF_MESSAGE = 0x01;
        private const uint NIF_ICON = 0x02;
        private const uint NIF_STATE = 0x08;
        private const uint NIF_INFO = 0x10;
        private const uint NIS_HIDDEN = 0x01;
        private const uint NIIF_NONE = 0x00;
        private const uint NIIF_WARNING = 0x02;
        private const uint NIIF_ERROR = 0x03;

        /// <summary>系统默认应用图标(IDC_APPLICATION);IntPtr 不能作 const,用 readonly。</summary>
        private static readonly IntPtr IDI_APPLICATION = new(32512);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NOTIFYICONDATAW
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;
            public uint dwState;
            public uint dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szInfo;
            public uint uVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string szInfoTitle;
            public uint dwInfoFlags;
            public Guid guidItem;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASSW
        {
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string? lpszMenuName;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string lpszClassName;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int ptX;
            public int ptY;
        }

        // 说明:这里用经典 DllImport 而不是 LibraryImport 源生成 ——
        // 后者要求项目允许不安全代码(SYSLIB1062),本项目的编译设置没有开 AllowUnsafeBlocks。

        [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATAW data);

        [DllImport("user32.dll", EntryPoint = "RegisterClassW", SetLastError = true)]
        private static extern ushort RegisterClass(ref WNDCLASSW lpWndClass);

        [DllImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true)]
        private static extern IntPtr CreateWindowExW(
            uint dwExStyle,
            [MarshalAs(UnmanagedType.LPWStr)] string lpClassName,
            [MarshalAs(UnmanagedType.LPWStr)] string lpWindowName,
            uint dwStyle,
            int x, int y, int nWidth, int nHeight,
            IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
        private static extern IntPtr DefWindowProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern int GetMessage(out MSG message, IntPtr hWnd, uint min, uint max);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TranslateMessage(ref MSG message);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref MSG message);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("shell32.dll")]
        private static extern int ExtractIconEx(
            [MarshalAs(UnmanagedType.LPWStr)] string fileName,
            int index,
            out IntPtr largeIcon,
            out IntPtr smallIcon,
            int count);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("user32.dll", EntryPoint = "LoadIconW")]
        private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr iconName);

        private static bool TryCreateHiddenWindow(out IntPtr window, out uint callbackMessage)
        {
            window = IntPtr.Zero;
            callbackMessage = 0xE000u + 0x0001u; // WM_APP + 1,不与系统消息撞车

            try
            {
                var className = "DSHLauncherNotificationWindow";
                var instance = Marshal.GetHINSTANCE(typeof(NotificationService).Module);

                var wndClass = new WNDCLASSW
                {
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(WndProcCallback),
                    hInstance = instance,
                    lpszClassName = className,
                };
                if (RegisterClass(ref wndClass) == 0)
                {
                    // 同名类已注册(理论上单实例下不会发生):继续尝试建窗口
                }

                // 不带 WS_VISIBLE:窗口从不显示,只作托盘回调的接收者
                window = CreateWindowExW(0, className, "DSH Launcher", 0,
                    0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
                return window != IntPtr.Zero;
            }
            catch (Exception ex)
            {
                AppLogService.Write($"[通知] 创建通知窗口失败: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);
    }
}
