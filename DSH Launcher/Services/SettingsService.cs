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
    /// 在应用内 WebView 里点击链接(页面请求新窗口/新标签,如 target="_blank"、window.open)时的处理方式。
    /// 枚举顺序与设置页下拉框的索引一一对应,不要随意调换。
    /// </summary>
    public enum WebViewLinkTarget
    {
        /// <summary>接管并由系统默认浏览器打开(默认)。</summary>
        SystemBrowser = 0,

        /// <summary>不接管,完全交给 WebView2 底层默认行为。</summary>
        AppWebView = 1,
    }

    /// <summary>
    /// 插件目录来源。**不是 npm registry**:社区有两份并行的人工策展目录,启动器都支持:
    /// <list type="bullet">
    /// <item><c>awesome-dsh-plugin</c> —— 社区市场 dsh-market 的数据源
    /// (https://awesome-dsh-plugin.com/plugins.json,顶层是对象 + <c>plugins</c> 数组)。</item>
    /// <item><c>dsh-plugin.org</c> —— 插件中心 dsh-plugin-hub 的数据源
    /// (https://api.dsh-plugin.org/plugins.zh.json,顶层是数组,字段是缩写)。
    /// 两份目录的插件集合与字段都不同,结构由启动器自动识别。</item>
    /// </list>
    /// 只列**已知结构**的目录:两份结构的字段完全不同(完整单词 vs 缩写),
    /// 让用户随便填个地址很可能解析成空列表或错字段,所以不提供“自定义地址”。
    /// 将来要接新市场时,在这里加一个成员 + <see cref="PluginService.ResolveCatalogUrl"/> 里加一条映射即可
    /// (若新市场结构不同,还要给 <c>PluginService.ParseCatalog</c> 加一个解析分支)。
    /// 枚举按**名字**解析(见 <see cref="LenientEnumConverter{T}"/>),所以增删成员不会读错已有设置。
    /// </summary>
    public enum PluginCatalogSource
    {
        /// <summary>awesome-dsh-plugin.com(dsh-market 的数据源),默认。</summary>
        Official = 0,

        /// <summary>dsh-plugin.org(dsh-plugin-hub 的数据源)。</summary>
        DshPluginOrg = 1,
    }

    /// <summary>应用设置(持久化为 JSON)。</summary>
    public sealed class AppSettings
    {
        /// <summary>单击托盘图标的动作,默认在 WebView 中打开。</summary>
        [JsonConverter(typeof(LenientEnumConverter<WebOpenAction>))]
        public WebOpenAction TraySingleClick { get; set; } = WebOpenAction.WebView;

        /// <summary>双击托盘图标的动作,默认在浏览器中打开。</summary>
        [JsonConverter(typeof(LenientEnumConverter<WebOpenAction>))]
        public WebOpenAction TrayDoubleClick { get; set; } = WebOpenAction.Browser;

        /// <summary>应用启动时自动运行 DSH 服务(仅在已安装时生效),默认关闭。</summary>
        public bool RunDshServiceOnStartup { get; set; }

        /// <summary>DSH 服务启动并检测到 Web 地址后的动作(设置页仅提供 None/WebView/Browser),默认无动作。</summary>
        [JsonConverter(typeof(LenientEnumConverter<WebOpenAction>))]
        public WebOpenAction AfterDshServiceStarted { get; set; } = WebOpenAction.None;

        /// <summary>应用启动时打开主界面;关闭时启动到系统托盘,默认打开。</summary>
        public bool ShowMainWindowOnStartup { get; set; } = true;

        /// <summary>
        /// dsh Web 服务监听端口。0 = 不指定,沿用 dsh 默认端口(3080),对应启动参数 --port。
        /// </summary>
        public int ListenPort { get; set; }

        /// <summary>在应用内 WebView 中点击链接(页面请求新窗口)时的打开方式,默认交给系统浏览器。</summary>
        [JsonConverter(typeof(LenientEnumConverter<WebViewLinkTarget>))]
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

        /// <summary>插件目录来源,默认 awesome-dsh-plugin。</summary>
        [JsonConverter(typeof(LenientEnumConverter<PluginCatalogSource>))]
        public PluginCatalogSource PluginCatalog { get; set; } = PluginCatalogSource.Official;

        /// <summary>“保留超时”的默认值(分钟)。</summary>
        public const int DefaultWebViewIdleTimeoutMinutes = 5;

        /// <summary>“保留超时”允许的最小值(分钟)。</summary>
        public const int MinWebViewIdleTimeoutMinutes = 1;

        /// <summary>“保留超时”允许的最大值(分钟,24 小时)。</summary>
        public const int MaxWebViewIdleTimeoutMinutes = 1440;
    }

    /// <summary>
    /// 宽容的字符串枚举转换器:无法识别的名称(例如设置文件里存着已被删除的枚举值)
    /// 回退为 <c>default</c>(即第一个成员,约定为各枚举的默认值),而不是抛异常。
    ///
    /// 必要性:属性上直接用 <see cref="JsonStringEnumConverter"/> 时,
    /// 遇到未知名称会抛 <c>JsonException</c>,而 <see cref="SettingsService.Load"/> 是整体回退,
    /// 结果是**所有设置静默重置为默认**(已实测)。故从枚举中删值属于破坏性变更,必须靠宽容解析兜住。
    /// </summary>
    internal sealed class LenientEnumConverter<T> : JsonConverter<T> where T : struct, Enum
    {
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String
                && Enum.TryParse<T>(reader.GetString(), out var fromName))
            {
                return fromName;
            }

            if (reader.TokenType == JsonTokenType.Number
                && reader.TryGetInt32(out var number)
                && Enum.IsDefined(typeof(T), number))
            {
                return (T)Enum.ToObject(typeof(T), number);
            }

            return default;
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.ToString());
    }

    /// <summary>
    /// 设置服务(单例)。文件保存在:
    /// Windows:%APPDATA%\DSH Launcher\Settings\settings.json;
    /// macOS:~/Library/Application Support/DSH Launcher/Settings/settings.json。
    /// 本机防御(仅 Windows 需要回退链)已由 <see cref="PlatformProcess.RoamingAppDataDirectory"/> 收敛。
    /// </summary>
    public sealed class SettingsService
    {
        public static SettingsService Instance { get; } = new();

        /// <summary>
        /// 设置文件路径。必须是延迟求值:应用启动极早期解析用户目录可能瞬时为空
        /// (Windows 上 SHGetKnownFolderPath 未就绪),静态字段会把空值永久固化;
        /// 每次使用时现算即可拿到正确路径。
        /// </summary>
        private static string SettingsFilePath => Path.Combine(
            PlatformProcess.RoamingAppDataDirectory, "DSH Launcher", "Settings", "settings.json");

        private static string GetRoamingAppDataDirectory() => PlatformProcess.RoamingAppDataDirectory;

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
