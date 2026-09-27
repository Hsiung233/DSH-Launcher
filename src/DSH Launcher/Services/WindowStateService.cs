using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;

namespace DSH_Launcher.Services
{
    /// <summary>窗口状态(设备本地状态,独立于用户偏好)。各窗口字段相互独立,互不覆盖。</summary>
    public sealed class WindowState
    {
        /// <summary>主窗口的位置/大小(可空:尚未保存过主窗口状态)。</summary>
        public WindowBounds? MainWindow { get; set; }

        /// <summary>WebView 窗口的位置/大小(可空:尚未保存过 WebView 窗口状态)。</summary>
        public WindowBounds? WebView { get; set; }
    }

    /// <summary>
    /// 单个窗口的"还原状态":位置、客户区尺寸与是否最大化。
    /// 位置为物理像素(PixelPoint),尺寸为设备无关单位(DIP);
    /// 无论窗口关闭时是否最大化,位置/尺寸始终是"最大化之前"的还原值。
    /// 注意:不要存物理像素尺寸再在恢复时除以 RenderScaling 换算——
    /// RenderScaling 在窗口未显示/已关闭时不可靠(会回退为 1),
    /// 保存与恢复时刻缩放不一致会导致窗口每次开关都缩水/放大一圈。
    /// </summary>
    public sealed class WindowBounds
    {
        public int X { get; set; }
        public int Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }

        /// <summary>关闭窗口时是否处于最大化状态(旧状态文件没有该字段,反序列化为 false)。</summary>
        public bool Maximized { get; set; }
    }

    /// <summary>
    /// 窗口状态服务(单例)。保存/恢复主窗口与 WebView 窗口的位置大小与最大化状态,
    /// 独立文件:%LOCALAPPDATA%\DSH Launcher\Settings\window-state.json(原子写入)。
    /// 各窗口字段相互独立,保存其一不会覆盖另一。
    /// 窗口的位置/尺寸通过 <see cref="WindowStateTracker"/> 在 Normal 状态下持续记录,
    /// 因此最大化窗口存下来的是还原尺寸而不是铺满工作区的尺寸。
    /// </summary>
    public sealed class WindowStateService
    {
        public static WindowStateService Instance { get; } = new();

        /// <summary>
        /// 状态文件路径。与 <see cref="SettingsService"/> / <see cref="AppLogService"/> 同样的防御:**延迟求值**,
        /// 应用启动极早期解析用户目录可能瞬时为空,用 static readonly 会把空路径永久固化。
        /// </summary>
        private static string StateFilePath => Path.Combine(
            PlatformProcess.LocalAppDataDirectory,
            "DSH Launcher", "Settings", "window-state.json");

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
        };

        private WindowStateService()
        {
        }

        /// <summary>读取完整状态;文件不存在或损坏时返回 null。</summary>
        private WindowState? ReadState()
        {
            try
            {
                if (!File.Exists(StateFilePath))
                {
                    return null;
                }

                var json = File.ReadAllText(StateFilePath);
                return JsonSerializer.Deserialize<WindowState>(json);
            }
            catch (Exception)
            {
                // 文件损坏等情况:当作无状态
                return null;
            }
        }

        /// <summary>原子写入状态(先写临时文件再替换)。保存某一窗口时保留其他窗口的字段。</summary>
        private void WriteState(Func<WindowState, WindowState> update)
        {
            try
            {
                var state = ReadState() ?? new WindowState();
                update(state);

                Directory.CreateDirectory(Path.GetDirectoryName(StateFilePath)!);
                var json = JsonSerializer.Serialize(state, JsonOptions);

                var tempPath = StateFilePath + ".tmp";
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, StateFilePath, overwrite: true);
            }
            catch (Exception)
            {
                // 保存失败不影响关闭/退出流程
            }
        }

        /// <summary>位置落在屏幕之外时允许的容差(物理像素),见 <see cref="Apply"/>。</summary>
        private const int ScreenTolerance = 64;

        /// <summary>读取上次保存的主窗口状态;无效时返回 null。</summary>
        private WindowBounds? LoadMainWindowState() => Validate(ReadState()?.MainWindow);

        /// <summary>读取 WebView 窗口状态;无效时返回 null。</summary>
        private WindowBounds? LoadWebViewState() => Validate(ReadState()?.WebView);

        /// <summary>过滤掉尺寸无效的记录。</summary>
        private static WindowBounds? Validate(WindowBounds? bounds) =>
            bounds is null || bounds.Width <= 0 || bounds.Height <= 0 ? null : bounds;

        /// <summary>创建主窗口状态跟踪器:窗口关闭/应用退出前调用其 Save() 写回状态。</summary>
        public WindowStateTracker TrackMainWindow(Window window, WindowBounds? initialBounds)
        {
            return new WindowStateTracker(window, initialBounds, bounds => WriteState(state =>
            {
                state.MainWindow = bounds;
                return state;
            }));
        }

        /// <summary>创建 WebView 窗口状态跟踪器:窗口关闭/应用退出前调用其 Save() 写回状态。</summary>
        public WindowStateTracker TrackWebViewWindow(Window window, WindowBounds? initialBounds)
        {
            return new WindowStateTracker(window, initialBounds, bounds => WriteState(state =>
            {
                state.WebView = bounds;
                return state;
            }));
        }

        /// <summary>
        /// 恢复 WebView 窗口位置/大小与最大化状态。
        /// 返回已应用的 bounds(可交给 <see cref="TrackWebViewWindow"/> 作为初始值);
        /// 无有效记录或屏幕校验失败时返回 null,调用方应改用默认尺寸。
        /// </summary>
        public WindowBounds? RestoreWebViewWindow(Window window) => Restore(window, LoadWebViewState());

        /// <summary>恢复主窗口位置/大小与最大化状态;屏幕校验失败时返回 null(保持默认位置/尺寸)。</summary>
        public WindowBounds? RestoreMainWindow(Window window) => Restore(window, LoadMainWindowState());

        private static WindowBounds? Restore(Window window, WindowBounds? bounds) => bounds is not null && Apply(window, bounds) ? bounds : null;

        /// <summary>
        /// 把位置/尺寸应用到窗口,并恢复最大化状态。
        /// 位置直接使用物理像素;尺寸为 DIP,若超出所在屏幕工作区则钳制到工作区大小
        /// (防止换到低分辨率显示器后窗口过大超出屏幕)。
        /// 应用后的实际值会写回 <paramref name="bounds"/>。
        /// </summary>
        private static bool Apply(Window window, WindowBounds bounds)
        {
            var screens = window.Screens?.All;
            if (screens is null || screens.Count == 0)
            {
                // 无法校验时直接应用,不钳制
                window.Width = bounds.Width;
                window.Height = bounds.Height;
                window.Position = new PixelPoint(bounds.X, bounds.Y);
                ApplyMaximized(window, bounds);
                return true;
            }

            var position = new PixelPoint(bounds.X, bounds.Y);
            var screen = screens.FirstOrDefault(s => s.Bounds.Contains(position));
            if (screen is null)
            {
                // 位置落在所有屏幕之外(如显示器已断开)。但最大化窗口的位置可能带边框偏移
                // (Windows 上是 -8,-8 这类值),旧状态文件里就会存下这种坐标,
                // 因此允许一点容差;命中后把左上角钳制回工作区,避免窗口整个跑到屏幕外。
                screen = screens.FirstOrDefault(s =>
                    position.X >= s.Bounds.X - ScreenTolerance &&
                    position.Y >= s.Bounds.Y - ScreenTolerance &&
                    position.X < s.Bounds.Right + ScreenTolerance &&
                    position.Y < s.Bounds.Bottom + ScreenTolerance);
                if (screen is null)
                {
                    return false;
                }

                var area = screen.WorkingArea;
                position = new PixelPoint(
                    Math.Clamp(position.X, area.X, Math.Max(area.X, area.Right - 1)),
                    Math.Clamp(position.Y, area.Y, Math.Max(area.Y, area.Bottom - 1)));
            }

            bounds.X = position.X;
            bounds.Y = position.Y;
            bounds.Width = Math.Min(bounds.Width, screen.WorkingArea.Width / screen.Scaling);
            bounds.Height = Math.Min(bounds.Height, screen.WorkingArea.Height / screen.Scaling);

            window.Width = bounds.Width;
            window.Height = bounds.Height;
            window.Position = position;

            ApplyMaximized(window, bounds);
            return true;
        }

        /// <summary>
        /// 恢复最大化状态。
        /// Win32 平台在"显示之前"设置 WindowState 会记在 _showWindowState 上,Show 时以
        /// SW_MAXIMIZE 显示(此时窗口不可见,平台只是记录状态),所以在 Show 之前设置即可。
        /// </summary>
        private static void ApplyMaximized(Window window, WindowBounds bounds)
        {
            if (bounds.Maximized)
            {
                window.WindowState = Avalonia.Controls.WindowState.Maximized;
            }
        }
    }

    /// <summary>
    /// 跟踪一个窗口的位置/尺寸,供窗口关闭/应用退出时保存(含最大化状态)。
    /// 窗口最大化后 Position/ClientSize 都是最大化状态下的值,直接保存会让下次打开变成
    /// "铺满工作区但没有最大化"的普通窗口(最小化时位置还会变成 -32000,-32000),
    /// 所以这里始终记住窗口处于 Normal 状态时的 bounds。
    /// </summary>
    public sealed class WindowStateTracker
    {
        private readonly Window _window;
        private readonly Action<WindowBounds> _persist;

        /// <summary>最后一次"非最大化"状态下的位置/尺寸。</summary>
        private WindowBounds? _normalBounds;

        /// <summary>是否已排队一次几何记录(合并连续变化,避免拖拽调整时刷队列)。</summary>
        private bool _capturePending;

        private bool _disposed;

        internal WindowStateTracker(Window window, WindowBounds? initialBounds, Action<WindowBounds> persist)
        {
            _window = window;
            _persist = persist;
            _normalBounds = initialBounds;

            // 事件在窗口关闭后随窗口一起被回收,无需显式退订。
            _window.PositionChanged += (_, _) => QueueCapture();
            _window.Resized += (_, _) => QueueCapture();
        }

        /// <summary>保存当前状态(窗口关闭/应用退出前调用)。</summary>
        public void Save()
        {
            if (_disposed)
            {
                return;
            }

            var state = _window.WindowState;

            // 只有 Normal 状态下 Position/ClientSize 才是"还原值";最小化时 Position 是
            // -32000,-32000 这类无效值,最大化时是铺满工作区的尺寸,都只能用记录下来的值。
            var bounds = state == Avalonia.Controls.WindowState.Normal || _normalBounds is null
                ? Capture(_window)
                : _normalBounds;
            bounds.Maximized = state == Avalonia.Controls.WindowState.Maximized;
            _persist(bounds);
        }

        /// <summary>窗口已关闭,后续不再保存(此时平台实现已销毁,Position 会回退为 0,0)。</summary>
        public void Dispose() => _disposed = true;

        /// <summary>
        /// 排队记录当前几何。必须延后一拍:
        /// Win32 平台在 WM_SIZE 里先触发 Resized(以及 WM_MOVE),之后才更新 WindowState,
        /// 在事件处理中读 WindowState 得到的还是变化前的值,会把最大化后的尺寸/位置
        /// 当成"还原值"记下来。这里用 Background 优先级,等本轮消息处理结束、界面空闲时再读。
        /// </summary>
        private void QueueCapture()
        {
            if (_disposed || _capturePending)
            {
                return;
            }

            _capturePending = true;
            Avalonia.Threading.Dispatcher.UIThread.Post(CaptureNormalBounds, Avalonia.Threading.DispatcherPriority.Background);
        }

        /// <summary>只记录 Normal 状态下的几何,最大化/最小化引起的变化一律忽略。</summary>
        private void CaptureNormalBounds()
        {
            _capturePending = false;

            if (_disposed || _window.WindowState != Avalonia.Controls.WindowState.Normal)
            {
                return;
            }

            _normalBounds = Capture(_window);
        }

        /// <summary>抓取窗口当前位置(物理像素)与客户区尺寸(DIP)的快照。</summary>
        private static WindowBounds Capture(Window window) => new()
        {
            X = window.Position.X,
            Y = window.Position.Y,
            Width = window.ClientSize.Width,
            Height = window.ClientSize.Height,
        };
    }
}
