using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DSH_Launcher.Services
{
    /// <summary>打开 Web 端的方式(或打开主界面/无动作)。</summary>
    public enum WebOpenAction
    {
        None = 0,
        WebView = 1,
        Browser = 2,
        MainWindow = 3,
    }

    /// <summary>
    /// 在应用内 WebView 里点击链接(页面请求新窗口/新标签,如 target="_blank"、window.open)时的打开方式。
    /// 枚举顺序与设置页下拉框的索引一一对应,不要随意调换。
    /// </summary>
    public enum WebViewLinkTarget
    {
        /// <summary>交给系统默认浏览器打开(默认)。</summary>
        SystemBrowser = 0,

        /// <summary>在应用内 WebView 窗口中加载(会替换掉当前页面)。</summary>
        AppWebView = 1,

        /// <summary>不接管,交给 WebView2 底层默认行为。</summary>
        Unhandled = 2,
    }

    /// <summary>应用设置(持久化为 JSON)。</summary>
    public sealed class AppSettings
    {
        /// <summary>单击托盘图标的动作,默认在 WebView 中打开。</summary>
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public WebOpenAction TraySingleClick { get; set; } = WebOpenAction.WebView;

        /// <summary>双击托盘图标的动作,默认在浏览器中打开。</summary>
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public WebOpenAction TrayDoubleClick { get; set; } = WebOpenAction.Browser;

        /// <summary>应用启动时自动运行 DSH 服务(仅在已安装时生效),默认关闭。</summary>
        public bool RunDshServiceOnStartup { get; set; }

        /// <summary>DSH 服务启动并检测到 Web 地址后的动作(设置页仅提供 None/WebView/Browser),默认无动作。</summary>
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public WebOpenAction AfterDshServiceStarted { get; set; } = WebOpenAction.None;

        /// <summary>应用启动时打开主界面;关闭时启动到系统托盘,默认打开。</summary>
        public bool ShowMainWindowOnStartup { get; set; } = true;

        /// <summary>
        /// dsh Web 服务监听端口。0 = 不指定,沿用 dsh 默认端口(3080),对应启动参数 --port。
        /// </summary>
        public int ListenPort { get; set; }

        /// <summary>在应用内 WebView 中点击链接(页面请求新窗口)时的打开方式,默认交给系统浏览器。</summary>
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public WebViewLinkTarget WebViewLink { get; set; } = WebViewLinkTarget.SystemBrowser;

        /// <summary>
        /// 关闭 WebView 窗口时是否保留(仅隐藏)窗口。开启则下次打开是毫秒级复用,
        /// 但会持续占用 WebView2 进程内存;关闭则立即释放、下次需重新加载。默认保留。
        /// </summary>
        public bool KeepWebViewAlive { get; set; } = true;

        /// <summary>
        /// 隐藏后保留 WebView 窗口的时长(分钟),超过则自动关闭并释放 WebView2 内存。
        /// 0 或超出范围 = 使用默认值 <see cref="DefaultWebViewIdleTimeoutMinutes"/> 分钟。
        /// 仅在 <see cref="KeepWebViewAlive"/> 开启时有效。
        /// </summary>
        public int WebViewIdleTimeoutMinutes { get; set; }

        /// <summary>“保留超时”的默认值(分钟)。</summary>
        public const int DefaultWebViewIdleTimeoutMinutes = 5;

        /// <summary>“保留超时”允许的最小值(分钟)。</summary>
        public const int MinWebViewIdleTimeoutMinutes = 1;

        /// <summary>“保留超时”允许的最大值(分钟,24 小时)。</summary>
        public const int MaxWebViewIdleTimeoutMinutes = 1440;
    }

    /// <summary>
    /// 设置服务(单例)。文件保存在 %LOCALAPPDATA%\DSH Launcher\Settings\settings.json。
    /// </summary>
    public sealed class SettingsService
    {
        public static SettingsService Instance { get; } = new();

        /// <summary>
        /// 设置文件路径。必须是延迟求值:实测应用启动极早期 GetFolderPath(ApplicationData)
        /// 可能瞬时返回空串(SHGetKnownFolderPath 未就绪),静态字段会把空值永久固化;
        /// 每次使用时现算即可拿到正确路径。
        /// </summary>
        private static string SettingsFilePath => Path.Combine(GetRoamingAppDataDirectory(), "DSH Launcher", "Settings", "settings.json");

        /// <summary>
        /// 解析 Roaming AppData 目录。
        /// 实测在本应用进程中 GetFolderPath(ApplicationData) 可能返回空串(原因不明,
        /// 同进程内 LocalApplicationData 正常),因此依次回退:%APPDATA% 环境变量 → LocalAppData。
        /// </summary>
        private static string GetRoamingAppDataDirectory()
        {
            var path = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrEmpty(path))
            {
                path = Environment.GetEnvironmentVariable("APPDATA") ?? string.Empty;
            }
            if (string.IsNullOrEmpty(path))
            {
                path = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            }
            return path;
        }

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
        };

        public AppSettings Settings { get; private set; } = new();

        /// <summary>设置被修改并保存后触发。</summary>
        public event Action? SettingsChanged;

        private SettingsService()
        {
            Load();
        }

        /// <summary>从磁盘加载设置;文件不存在或损坏时保持默认值。</summary>
        public void Load()
        {
            try
            {
                if (File.Exists(SettingsFilePath))
                {
                    var json = File.ReadAllText(SettingsFilePath);
                    Settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                    DshService.Instance.AppendSystemLog($"[设置] 已从 {SettingsFilePath} 加载: 单击={Settings.TraySingleClick} 双击={Settings.TrayDoubleClick}");
                }
            }
            catch (Exception ex)
            {
                DshService.Instance.AppendSystemLog($"[设置] 加载失败(回退默认值): {ex.GetType().Name}: {ex.Message}");
                // 文件损坏时回退到默认设置
                Settings = new AppSettings();
            }
        }

        /// <summary>更新设置并立即持久化。</summary>
        public void Update(Action<AppSettings> update)
        {
            update(Settings);
            Save();
            SettingsChanged?.Invoke();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsFilePath)!);
                File.WriteAllText(SettingsFilePath, JsonSerializer.Serialize(Settings, JsonOptions));
            }
            catch (Exception)
            {
                // 保存失败不影响运行
            }
        }
    }
}
