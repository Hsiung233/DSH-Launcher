using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;

namespace DSH_Launcher.Services
{
    /// <summary>主窗口的位置/大小(设备本地状态,独立于用户偏好)。</summary>
    public sealed class WindowState
    {
        public int X { get; set; }
        public int Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }

        /// <summary>WebView 窗口的位置/大小(可空:老配置文件里没有该字段)。</summary>
        public WindowBounds? WebView { get; set; }
    }

    /// <summary>
    /// 单个窗口的位置与客户区尺寸。
    /// 位置为物理像素(PixelPoint),尺寸为设备无关单位(DIP)。
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
    }

    /// <summary>
    /// 窗口状态服务(单例)。保存/恢复主窗口与 WebView 窗口的位置大小,
    /// 独立文件:%LOCALAPPDATA%\DSH Launcher\Settings\window-state.json(原子写入)。
    /// 各窗口字段相互独立,保存其一不会覆盖另一。
    /// </summary>
    public sealed class WindowStateService
    {
        public static WindowStateService Instance { get; } = new();

        private static readonly string StateFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
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

        /// <summary>读取上次保存的主窗口状态;无效时返回 null。</summary>
        public WindowState? LoadMainWindowState()
        {
            var state = ReadState();
            if (state is null || state.Width <= 0 || state.Height <= 0)
            {
                return null;
            }

            return state;
        }

        /// <summary>保存主窗口当前的位置/大小(保留 WebView 字段)。</summary>
        public void SaveMainWindow(Window window)
        {
            WriteState(state =>
            {
                state.X = window.Position.X;
                state.Y = window.Position.Y;
                state.Width = window.ClientSize.Width;
                state.Height = window.ClientSize.Height;
                return state;
            });
        }

        /// <summary>保存 WebView 窗口当前的位置/大小(保留主窗口字段)。</summary>
        public void SaveWebViewWindow(Window window)
        {
            WriteState(state =>
            {
                state.WebView = new WindowBounds
                {
                    X = window.Position.X,
                    Y = window.Position.Y,
                    Width = window.ClientSize.Width,
                    Height = window.ClientSize.Height,
                };
                return state;
            });
        }

        /// <summary>读取 WebView 窗口状态;无效时返回 null。</summary>
        public WindowBounds? LoadWebViewState()
        {
            var state = ReadState();
            var bounds = state?.WebView;
            if (bounds is null || bounds.Width <= 0 || bounds.Height <= 0)
            {
                return null;
            }

            return bounds;
        }

        /// <summary>
        /// 恢复 WebView 窗口位置/大小。
        /// 屏幕校验:保存的位置不在任何显示器上时(如拔掉外接显示器)不应用,
        /// 并把尺寸钳制到所在屏幕工作区内。
        /// 返回 true 表示已应用恢复的位置/大小,false 表示应使用默认尺寸。
        /// </summary>
        public bool RestoreWebViewWindow(Window window)
        {
            var bounds = LoadWebViewState();
            if (bounds is null)
            {
                return false;
            }

            return Apply(window, bounds.X, bounds.Y, bounds.Width, bounds.Height);
        }

        /// <summary>恢复主窗口位置/大小。屏幕校验失败时返回 false(保持默认位置)。</summary>
        public bool RestoreMainWindow(Window window)
        {
            var state = LoadMainWindowState();
            if (state is null)
            {
                return false;
            }

            return Apply(window, state.X, state.Y, state.Width, state.Height);
        }

        /// <summary>
        /// 把位置/尺寸应用到窗口。
        /// 位置直接使用物理像素;尺寸为 DIP,若超出所在屏幕工作区则钳制到工作区大小
        /// (防止换到低分辨率显示器后窗口过大超出屏幕)。
        /// </summary>
        private static bool Apply(Window window, int x, int y, double widthDip, double heightDip)
        {
            var screens = window.Screens?.All;
            if (screens is null || screens.Count == 0)
            {
                // 无法校验时直接应用,不钳制
                window.Width = widthDip;
                window.Height = heightDip;
                window.Position = new PixelPoint(x, y);
                return true;
            }

            var position = new PixelPoint(x, y);
            var screen = screens.FirstOrDefault(s => s.Bounds.Contains(position));
            if (screen is null)
            {
                return false; // 位置不在任何屏幕上(如显示器已断开)
            }

            var scaling = screen.Scaling;
            window.Width = Math.Min(widthDip, screen.WorkingArea.Width / scaling);
            window.Height = Math.Min(heightDip, screen.WorkingArea.Height / scaling);
            window.Position = position;
            return true;
        }
    }
}
