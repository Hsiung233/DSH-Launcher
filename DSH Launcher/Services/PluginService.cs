using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DSH_Launcher.Services
{
    /// <summary>
    /// 一个插件条目。两种来源合并成同一份列表:
    /// ① profile 的 <c>package.json</c> 里 <c>dependencies</c> 声明的包(用户安装的插件,可卸载);
    /// ② <c>dsh --dump-config</c> 组合出来的 Loader 条目(带条目 id,可启用/禁用)。
    /// <para>
    /// ⚠ 实现 <see cref="INotifyPropertyChanged"/> 是为了批量选择的 <see cref="IsSelected"/>:
    /// 两个列表都是**虚拟化**的(`ItemsControl` + `VirtualizingStackPanel` 会回收容器),
    /// 把选中态存在容器/控件上是错的 —— 必须存在数据对象上才能跨回收存活。
    /// </para>
    /// </summary>
    public sealed class PluginEntry : INotifyPropertyChanged
    {
        private bool _isSelected;

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// 批量操作是否勾选了这个条目(仅界面状态,不影响 profile)。
        /// 注意:「已安装」左列与「全部组合条目」右列**共用同一批实例**
        /// (左列是从右列里筛出 IsInstalled 的),所以两边勾选状态天然同步。
        /// </summary>
        public bool IsSelected
        {
            get => this._isSelected;
            set
            {
                if (this._isSelected == value)
                {
                    return;
                }

                this._isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.IsSelected)));
            }
        }

        /// <summary>Loader 条目 id(用于 cordis.patch.yml 的启停覆盖);不在组合树里时为空。</summary>
        public string Id { get; init; } = string.Empty;

        /// <summary>包名(组合树里的模块标识通常是包名;本地路径插件会保留原样)。</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>已安装版本;读不到时为 null。</summary>
        public string? Version { get; init; }

        /// <summary>是否在 profile 的 dependencies 里(即用户安装的插件,可由启动器卸载)。</summary>
        public bool IsInstalled { get; init; }

        /// <summary>是否在 dsh.profile.bundles 里(即参与了 profile 层组合)。</summary>
        public bool IsBundle { get; init; }

        /// <summary>组合树里的有效启用状态(被禁用的祖先组会体现在这里)。</summary>
        public bool IsActive { get; init; } = true;

        /// <summary>是否出现在组合树里(能拿到条目 id 才谈得上启停)。</summary>
        public bool InComposition { get; init; }

        /// <summary>cordis.patch.yml 的托管区块里是否已有该条目 id 的覆盖(有则可“恢复默认”)。</summary>
        public bool HasOverride { get; init; }

        /// <summary>是否为 dsh 自带的层(bundle 且不是依赖;卸载会破坏 profile,故不提供卸载)。</summary>
        public bool IsBuiltInBundle => this.IsBundle && !this.IsInstalled;

        /// <summary>能否启用/禁用:须在组合树里且有条目 id。</summary>
        public bool CanToggle => this.InComposition && this.Id.Length > 0;

        /// <summary>能否卸载:只有 dependencies 里的包可由 pnpm 移除。</summary>
        public bool CanUninstall => this.IsInstalled;

        public string VersionText => string.IsNullOrEmpty(this.Version) ? "未知版本" : "v" + this.Version;

        /// <summary>来源说明:内置层 / 已安装的层 / 已安装但不是层。</summary>
        public string OriginText => this switch
        {
            { IsBundle: true, IsInstalled: true } => "已安装 · 层",
            { IsBundle: true } => "自带层",
            { IsInstalled: true } => "已安装 · 非层",
            _ => "组合条目",
        };

        /// <summary>状态说明:已启用 / 已禁用 / 未参与组合。</summary>
        public string StateText => this.InComposition
            ? (this.IsActive ? "已启用" : "已禁用")
            : "未参与组合";

        /// <summary>启停按钮的文案(不按当前状态命名:按钮执行的是切换动作)。</summary>
        public string ToggleText => this.IsActive ? "禁用" : "启用";

        public string ToggleToolTip => this.CanToggle
            ? "在 profile 的 cordis.patch.yml 里写入启停覆盖(重启/重载后生效)"
            : "该条目不在组合树里,无法启停";
    }

    /// <summary>
    /// 策展目录里的一条插件。两份社区目录会被归一成这一个模型:
    /// <list type="bullet">
    /// <item><c>awesome-dsh-plugin</c>(dsh-market 的数据源):顶层对象 + <c>plugins</c> 数组,字段是完整单词。</item>
    /// <item><c>dsh-plugin.org</c>(dsh-plugin-hub 的数据源):顶层数组,字段是缩写
    /// (<c>n/o/c/d/ic/igc/v/sg/fk/vr</c>),且按语言分文件。</item>
    /// </list>
    /// </summary>
    public sealed class PluginCatalogEntry
    {
        public string Name { get; init; } = string.Empty;

        /// <summary>GitHub 仓库所有者。</summary>
        public string Owner { get; init; } = string.Empty;

        /// <summary>仓库地址(GitHub)。</summary>
        public string RepoUrl { get; init; } = string.Empty;

        /// <summary>目录站点上的插件页地址。</summary>
        public string PageUrl { get; init; } = string.Empty;

        /// <summary>分类 id(两份目录的键各自独立)。</summary>
        public string Category { get; init; } = string.Empty;

        /// <summary>分类显示名(取目录给的中文名)。</summary>
        public string CategoryText { get; init; } = string.Empty;

        /// <summary>中文描述。</summary>
        public string DescriptionZh { get; init; } = string.Empty;

        /// <summary>英文描述(中文缺失时回退用)。</summary>
        public string DescriptionEn { get; init; } = string.Empty;

        /// <summary>目录声明的 npm 包名;GitHub-only 的插件为空。</summary>
        public string Npm { get; init; } = string.Empty;

        public string Version { get; init; } = string.Empty;

        public int Stars { get; init; }

        /// <summary>目录里没有下载量数据时为 null(dsh-plugin.org 不提供)。</summary>
        public int? Downloads { get; init; }

        /// <summary>Fork 数(只有 dsh-plugin.org 提供;awesome-dsh-plugin 没有)。</summary>
        public int? Forks { get; init; }

        /// <summary>是否被目录标记为「已人工验证」(dsh-plugin.org 的 v=verified)。</summary>
        public bool Verified { get; init; }

        /// <summary>收录日期(YYYY-MM-DD)。</summary>
        public string Added { get; init; } = string.Empty;

        /// <summary>
        /// 目录给的首选安装规格 —— 从现成的 <c>dsh plugin --profile web add &lt;spec&gt;</c> 里取出:
        /// awesome-dsh-plugin 取 <c>install</c>(npm 包优先,没有 npm 包时是 <c>github:owner/repo</c>);
        /// dsh-plugin.org 取 <c>ic</c>(npm 通道)。
        /// </summary>
        public string InstallSpec { get; init; } = string.Empty;

        /// <summary>
        /// 备选安装规格(首选项装不上时回退):
        /// awesome-dsh-plugin 的 <c>tarball</c>(作者预构建的 GitHub Release),
        /// dsh-plugin.org 的 <c>igc</c>(GitHub 源码通道)。
        /// </summary>
        public string FallbackInstallSpec { get; init; } = string.Empty;

        /// <summary>列表里展示的描述(中文优先)。</summary>
        public string Description => string.IsNullOrWhiteSpace(this.DescriptionZh) ? this.DescriptionEn : this.DescriptionZh;

        public string VersionText => this.Version.Length == 0 ? string.Empty : "v" + this.Version;

        /// <summary>来源标记:走 npm 包还是 GitHub 源码。</summary>
        public string SourceText => this.Npm.Length > 0 ? "npm" : "GitHub";

        /// <summary>作者标记。</summary>
        public string OwnerText => this.Owner.Length == 0 ? string.Empty : "@" + this.Owner;

        /// <summary>星标 / 下载 / Fork / 分类组成的一行摘要(只显示目录确实提供的项)。</summary>
        public string MetaText
        {
            get
            {
                var parts = new List<string> { $"★ {FormatCount(this.Stars)}" };
                if (this.Downloads is int downloads)
                {
                    parts.Add($"下载 {FormatCount(downloads)}");
                }

                if (this.Forks is int forks and > 0)
                {
                    parts.Add($"Fork {FormatCount(forks)}");
                }

                if (this.CategoryText.Length > 0)
                {
                    parts.Add(this.CategoryText);
                }

                if (this.Added.Length > 0)
                {
                    parts.Add("收录 " + this.Added);
                }

                return string.Join("  ·  ", parts);
            }
        }

        /// <summary>目录里没有安装规格时给出可用的回退规格。</summary>
        public string EffectiveInstallSpec => this.InstallSpec.Length > 0
            ? this.InstallSpec
            : (this.Npm.Length > 0
                ? this.Npm
                : (this.Owner.Length > 0 ? $"github:{this.Owner}/{this.Name}" : string.Empty));

        private static string FormatCount(int value) => value >= 10000
            ? $"{value / 1000.0:0.#}k"
            : value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 一份策展目录快照。上游刻意**不做陈旧缓存**("a stale answer is not a degraded one but a wrong one"),
    /// 所以启动器只在本次会话内缓存,刷新时会重新拉取。
    /// </summary>
    public sealed class PluginCatalog
    {
        public IReadOnlyList<PluginCatalogEntry> Entries { get; init; } = [];

        /// <summary>分类 id → 中文显示名。</summary>
        public IReadOnlyDictionary<string, string> Categories { get; init; }
            = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>目录自身的更新时间(目录里的 updated 字段)。</summary>
        public string Updated { get; init; } = string.Empty;

        /// <summary>本次实际拉取的地址。</summary>
        public string SourceUrl { get; init; } = string.Empty;

        /// <summary>拉取到本地的时间。</summary>
        public DateTime FetchedAtLocal { get; init; }
    }

    /// <summary>目录里的排序方式。</summary>
    public enum PluginCatalogSort
    {
        /// <summary>按星标降序(默认)。</summary>
        Stars = 0,

        /// <summary>按下载量降序。</summary>
        Downloads = 1,

        /// <summary>按收录日期降序(新收录的在前)。</summary>
        Newest = 2,
    }

    /// <summary>一次插件盘点的结果。</summary>
    public sealed class PluginSnapshot
    {
        /// <summary>已安装的插件(dependencies 里的包)。</summary>
        public IReadOnlyList<PluginEntry> Installed { get; init; } = [];

        /// <summary>组合出来的全部 Loader 条目(dsh 自带的内部插件也在内)。</summary>
        public IReadOnlyList<PluginEntry> AllEntries { get; init; } = [];

        /// <summary>pnpm 是否可用(安装/卸载都经 dsh plugin 转发给 pnpm)。</summary>
        public bool PnpmAvailable { get; init; }

        /// <summary>盘点过程中的非致命问题(读清单失败、dump-config 报错等)。</summary>
        public string? Warning { get; init; }
    }

    /// <summary>
    /// 插件管理(单例)。插件的真实载体是 dsh 的 profile:
    /// 目录 <c>&lt;DSH_HOME&gt;/profiles/&lt;profile&gt;</c> 是一个 pnpm 项目,插件就是它的 dependencies,
    /// 参与组合的包写在 <c>package.json</c> 的 <c>dsh.profile.bundles</c> 里(由 dsh 自己维护)。
    ///
    /// 命令一律走 <c>dsh plugin --profile &lt;name&gt; ...</c>(官方入口:转发给 profile 目录下的 pnpm,
    /// 并在成功后重新对齐 bundles 列表),启动器不直接调 pnpm,以免与官方行为分叉。
    ///
    /// 启停没有官方 CLI:启用/禁用通过往 profile 的 <c>cordis.patch.yml</c> 里写 id 定向的
    /// <c>disabled</c> 覆盖实现(dsh 的 Web 设置页也把"写回启停"列为未做的后续工作,
    /// 见 @deepseek-ai/dsh-client-ui-settings-plugin-inventory 的已知限制)。
    /// 启动器只维护标记注释之间的区块,用户自己的补丁条目不受影响。
    /// </summary>
    public sealed partial class PluginService
    {
        public static PluginService Instance { get; } = new();

        /// <summary>插件所在的 profile。启动器运行的是 <c>dsh web</c>,对应 profile 名 web。</summary>
        public const string ProfileName = "web";

        /// <summary>托管区块的开始标记(cordis.patch.yml 里以注释形式存在)。</summary>
        private const string ManagedBegin = "# >>> DSH Launcher 插件启停(以下区块由启动器维护,手动修改可能被覆盖)";

        /// <summary>托管区块的结束标记。</summary>
        private const string ManagedEnd = "# <<< DSH Launcher 插件启停";

        private static readonly JsonDocumentOptions ManifestReadOptions = new()
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        };

        private readonly StringBuilder _log = new();

        /// <summary>
        /// 日志缓冲区的锁。日志会被多个线程写:RunDshStreamingAsync 的 stdout/stderr 回调(线程池)
        /// 与 UI 线程的操作日志;读侧还有 LogText 与 ClearLog。StringBuilder 不是线程安全的。
        /// </summary>
        private readonly Lock _logLock = new();

        private volatile bool _busy;

        /// <summary>
        /// 插件变更操作的互斥量。安装/卸载都由 <c>dsh plugin</c> 转发给 profile 目录里的 pnpm,
        /// 改的是同一份 package.json / pnpm-lock.yaml —— 两个操作并发执行会互相覆盖。
        /// 用非阻塞获取(<c>Wait(0)</c>):拿不到就直接拒绝并提示,而不是排队 ——
        /// 排队只是把同一个破坏性操作稍后再执行一遍。
        /// </summary>
        private readonly SemaphoreSlim _operationGate = new(1, 1);

        /// <summary>因已有插件操作在进行而拒绝执行的返回码(不是命令自身的退出码)。</summary>
        public const int RejectedExitCode = -2;

        /// <summary>
        /// 拉取插件目录用的 HTTP 客户端。设置 User-Agent:部分 CDN/镜像会拒绝没有 UA 的请求。
        /// 目录 3-7MB,给 60 秒超时。
        /// <para>
        /// ⚠ 它是**按代理设置现取**的:代理/不走代理列表一变就重建客户端
        /// (子进程的环境变量对进程内的 HttpClient 无效,只能建在 handler 上;而设置改了不该要求重启应用)。
        /// </para>
        /// </summary>
        private static readonly object HttpSync = new();
        private static HttpClient? _http;
        private static string _httpProxyKey = string.Empty;

        private static HttpClient Http => GetHttpClient();

        private static HttpClient GetHttpClient()
        {
            var key = ChildEnvironment.ProxyKey();
            lock (HttpSync)
            {
                if (_http is null || !string.Equals(key, _httpProxyKey, StringComparison.Ordinal))
                {
                    _http?.Dispose();
                    _http = CreateHttpClient();
                    _httpProxyKey = key;
                }

                return _http;
            }
        }

        private static HttpClient CreateHttpClient()
        {
            var handler = new HttpClientHandler();
            ChildEnvironment.ApplyProxy(handler);

            var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("DSH-Launcher/1.0");
            return client;
        }

        /// <summary>输出进插件页日志面板的一行文本(可能在后台线程触发)。</summary>
        public event Action<string>? LogAppended;

        /// <summary>忙碌状态变化(可能在后台线程触发)。</summary>
        public event Action? StateChanged;

        /// <summary>日志被清空。</summary>
        public event Action? LogsCleared;

        /// <summary>是否有安装/卸载/启停操作正在进行。</summary>
        public bool IsBusy => this._busy;

        public string LogText
        {
            get
            {
                lock (this._logLock)
                {
                    return this._log.ToString();
                }
            }
        }

        /// <summary>profile 目录(&lt;DSH_HOME&gt;/profiles/&lt;profile&gt;)。</summary>
        public static string ProfileDirectory => Path.Combine(DshHomeDirectory, "profiles", ProfileName);

        /// <summary>profile 的 package.json(dependencies 与 dsh.profile.bundles 都在这)。</summary>
        public static string ManifestPath => Path.Combine(ProfileDirectory, "package.json");

        /// <summary>profile 的用户补丁层(启停覆盖写在这里)。</summary>
        public static string PatchFilePath => Path.Combine(ProfileDirectory, "cordis.patch.yml");

        /// <summary>
        /// DSH 主目录:<c>DSH_HOME</c> 环境变量优先,否则 <c>~/.dsh</c>。
        /// 必须延迟求值(与 AppLogService/SettingsService 同样的防御:早期取用户目录可能瞬时为空)。
        /// </summary>
        private static string DshHomeDirectory
        {
            get
            {
                var fromEnv = Environment.GetEnvironmentVariable("DSH_HOME");
                if (!string.IsNullOrWhiteSpace(fromEnv))
                {
                    return fromEnv.Trim();
                }

                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (string.IsNullOrEmpty(home))
                {
                    home = Environment.GetEnvironmentVariable("HOME") ?? string.Empty;
                }

                return Path.Combine(home, ".dsh");
            }
        }

        private PluginService()
        {
        }

        /// <summary>记录一行插件页日志(同时写入 app.log,便于事后排查)。</summary>
        public void AppendLog(string message)
        {
            var line = message.EndsWith('\n') ? message : message + "\r\n";
            lock (this._logLock)
            {
                this._log.Append(line);
            }

            // 事件在锁外触发:处理器会 Post 到 UI 线程,放在锁里没有好处,只会扩大临界区
            LogAppended?.Invoke(line);
        }

        /// <summary>记录一条带前缀的应用级日志。</summary>
        public void AppendSystemLog(string message)
        {
            this.AppendLog($"[插件] {message}");
            AppLogService.Write($"[插件] {message}");
        }

        public void ClearLog()
        {
            lock (this._logLock)
            {
                this._log.Clear();
            }

            LogsCleared?.Invoke();
        }

        /// <summary>awesome-dsh-plugin 目录地址(社区市场 dsh-market 用的就是这份数据)。</summary>
        public const string AwesomeDshPluginCatalogUrl = "https://awesome-dsh-plugin.com/plugins.json";

        /// <summary>dsh-plugin.org 目录地址(插件中心 dsh-plugin-hub 用的就是这份数据,中文版)。</summary>
        public const string DshPluginOrgCatalogUrl = "https://api.dsh-plugin.org/plugins.zh.json";

        /// <summary>当前设置对应的目录地址。</summary>
        public static string ResolveCatalogUrl() => SettingsService.Instance.Settings.PluginCatalog switch
        {
            PluginCatalogSource.DshPluginOrg => DshPluginOrgCatalogUrl,
            _ => AwesomeDshPluginCatalogUrl,
        };

        /// <summary>
        /// 按目录地址缓存已解析的目录,键就是地址。
        /// <para>
        /// 为什么需要它:两个目录各要 0.1-8 秒才能拉完(3.4MB / 6.6MB),
        /// 用户来回切来源时每次重下既慢又容易看到错的中转状态;切回已拉过的来源应当是瞬时的。
        /// </para>
        /// <para>
        /// 缓存**天然有界**:地址只可能来自 <see cref="PluginCatalogSource"/> 的枚举(目前 2 个),
        /// 所以最多 2 条,不需要淘汰策略。条目总数 ~1.3 万,内存占用可接受。
        /// </para>
        /// <para>
        /// 与上游的区别:上游刻意不做**陈旧副本**(“a stale answer is not a degraded one but a wrong one”),
        /// 所以这里也只在本次运行的内存里留,不落盘、不跨进程,
        /// 并且始终可以用「刷新目录」强制重拉。
        /// </para>
        /// </summary>
        private readonly Dictionary<string, PluginCatalog> _catalogCache = new(StringComparer.Ordinal);

        /// <summary>取某个地址已缓存的目录;url 为空表示当前设置对应的地址。没有则返回 null。</summary>
        public PluginCatalog? GetCachedCatalog(string? url = null)
            => this._catalogCache.TryGetValue(url ?? ResolveCatalogUrl(), out var cached) ? cached : null;

        /// <summary>
        /// 拉取策展目录并解析。命中缓存时直接返回(切换来源、切回页签都不重复下载);
        /// forceReload=true(点「刷新目录」)时绕过缓存重新拉,并覆盖缓存。
        /// 失败时抛异常,由调用方展示原因 —— 上游也是这个态度:不拿旧数据冒充答案。
        /// </summary>
        public async Task<PluginCatalog> LoadCatalogAsync(bool forceReload = false)
        {
            var url = ResolveCatalogUrl();

            if (!forceReload && this._catalogCache.TryGetValue(url, out var cached))
            {
                return cached;
            }

            var started = DateTime.Now;
            try
            {
                using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseContentRead);
                response.EnsureSuccessStatusCode();
                var downloadedAt = DateTime.Now;

                using var stream = await response.Content.ReadAsStreamAsync();
                using var document = await JsonDocument.ParseAsync(stream);
                var parsedAt = DateTime.Now;

                var catalog = ParseCatalog(document.RootElement, url);

                this._catalogCache[url] = catalog;
                var updated = catalog.Updated.Length > 0 ? $",目录更新于 {catalog.Updated}" : string.Empty;
                this.AppendSystemLog($"已读取插件目录 {url}:{catalog.Entries.Count} 个插件"
                    + $"(下载 {(downloadedAt - started).TotalMilliseconds:0}ms"
                    + $" / 解析 {(parsedAt - downloadedAt).TotalMilliseconds:0}ms"
                    + $" / 映射 {(DateTime.Now - parsedAt).TotalMilliseconds:0}ms{updated})");
                return catalog;
            }
            catch (Exception ex)
            {
                this.AppendSystemLog($"读取插件目录失败({url}):{ex.GetType().Name}: {ex.Message}"
                    + $"(耗时 {(DateTime.Now - started).TotalSeconds:0.0}s)");
                throw;
            }
        }

        /// <summary>
        /// 按关键词 / 分类过滤并排序目录条目。
        /// keyword 匹配包名、作者、以及中英文描述;category 为空表示全部分类。
        /// </summary>
        public static IReadOnlyList<PluginCatalogEntry> FilterCatalog(
            PluginCatalog catalog,
            string keyword,
            string category,
            PluginCatalogSort sort)
        {
            IEnumerable<PluginCatalogEntry> query = catalog.Entries;

            if (!string.IsNullOrWhiteSpace(category))
            {
                query = query.Where(entry => string.Equals(entry.Category, category, StringComparison.Ordinal));
            }

            keyword = keyword.Trim();
            if (keyword.Length > 0)
            {
                query = query.Where(entry =>
                    entry.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                    || entry.Owner.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                    || entry.DescriptionZh.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                    || entry.DescriptionEn.Contains(keyword, StringComparison.OrdinalIgnoreCase));
            }

            /// <summary>按目录给的排序方式排;没有下载量的目录(dsh-plugin.org)按 0 参与排序。</summary>
            query = sort switch
            {
                PluginCatalogSort.Downloads => query.OrderByDescending(e => e.Downloads ?? 0).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase),
                PluginCatalogSort.Newest => query.OrderByDescending(e => e.Added, StringComparer.Ordinal).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase),
                _ => query.OrderByDescending(e => e.Stars).ThenByDescending(e => e.Downloads ?? 0).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase),
            };

            return query.ToList();
        }

        /// <summary>
        /// 解析目录 JSON。**自动识别两份社区目录的结构**:
        /// 顶层是对象且带 <c>plugins</c> → awesome-dsh-plugin(dsh-market 的数据源);
        /// 顶层是数组 → dsh-plugin.org(dsh-plugin-hub 的数据源,字段是缩写)。
        /// </summary>
        private static PluginCatalog ParseCatalog(JsonElement root, string sourceUrl)
            => root.ValueKind == JsonValueKind.Array
                ? ParseDshPluginOrgCatalog(root, sourceUrl)
                : ParseAwesomeDshPluginCatalog(root, sourceUrl);

        /// <summary>awesome-dsh-plugin 结构:顶层 <c>categories</c> + <c>plugins</c>,字段是完整单词。</summary>
        private static PluginCatalog ParseAwesomeDshPluginCatalog(JsonElement root, string sourceUrl)
        {
            var categories = new Dictionary<string, string>(StringComparer.Ordinal);
            if (root.TryGetProperty("categories", out var cats) && cats.ValueKind == JsonValueKind.Object)
            {
                foreach (var item in cats.EnumerateObject())
                {
                    var zh = ReadString(item.Value, "zh");
                    var en = ReadString(item.Value, "en");
                    categories[item.Name] = zh.Length > 0 ? zh : (en.Length > 0 ? en : item.Name);
                }
            }

            var entries = new List<PluginCatalogEntry>();
            if (root.TryGetProperty("plugins", out var plugins) && plugins.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in plugins.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var category = ReadString(item, "category");
                    entries.Add(new PluginCatalogEntry
                    {
                        Name = ReadString(item, "name"),
                        Owner = ReadString(item, "owner"),
                        RepoUrl = ReadString(item, "url"),
                        PageUrl = ReadString(item, "page"),
                        Category = category,
                        CategoryText = categories.TryGetValue(category, out var label) ? label : category,
                        DescriptionZh = ReadString(item, "description", "zh"),
                        DescriptionEn = ReadString(item, "description", "en"),
                        Npm = ReadString(item, "npm"),
                        Version = ReadString(item, "version"),
                        Stars = ReadInt(item, "stars"),
                        Downloads = ReadNullableInt(item, "downloads"),
                        Added = ReadString(item, "added"),
                        InstallSpec = ExtractInstallSpec(ReadString(item, "install")),
                        FallbackInstallSpec = ReadString(item, "tarball"),
                    });
                }
            }

            return new PluginCatalog
            {
                Entries = entries,
                Categories = categories,
                Updated = ReadString(root, "updated"),
                SourceUrl = sourceUrl,
                FetchedAtLocal = DateTime.Now,
            };
        }

        /// <summary>
        /// dsh-plugin.org 结构(dsh-plugin-hub 的数据源):顶层就是插件数组,字段是缩写 ——
        /// <c>n</c>=名字、<c>o</c>=作者、<c>c</c>=分类、<c>d</c>=描述、<c>r</c>={repo,npmPackage}、
        /// <c>ic</c>/<c>igc</c>=npm/GitHub 两条安装命令、<c>v</c>=验证状态、<c>sg</c>/<c>fk</c>=星标/Fork、
        /// <c>a</c>=收录日期、<c>vr</c>=版本(只有部分条目有)。
        /// 分类中文名不在数据里,取上游 <c>CATEGORY_LABELS</c> 的同一份映射。
        /// </summary>
        private static PluginCatalog ParseDshPluginOrgCatalog(JsonElement root, string sourceUrl)
        {
            var entries = new List<PluginCatalogEntry>();
            foreach (var item in root.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var owner = ReadString(item, "o");
                var name = ReadString(item, "n");
                var slug = ReadString(item, "s");
                if (slug.Length == 0)
                {
                    slug = name;
                }

                var repo = item.TryGetProperty("r", out var r) ? r : default;
                var repoPath = ReadString(repo, "repo");
                var npm = ReadString(repo, "npmPackage");
                var category = ReadString(item, "c");

                entries.Add(new PluginCatalogEntry
                {
                    Name = name,
                    Owner = owner,
                    RepoUrl = repoPath.Length > 0 ? "https://github.com/" + repoPath : string.Empty,
                    PageUrl = $"https://dsh-plugin.org/plugins/{owner}/{slug}",
                    Category = category,
                    CategoryText = DshPluginOrgCategories.TryGetValue(category, out var label) ? label : category,
                    // plugins.zh.json 的 d 字段本身就是中文
                    DescriptionZh = ReadString(item, "d"),
                    Npm = npm,
                    Version = ReadString(item, "vr").TrimStart('v', 'V'),
                    Stars = ReadInt(item, "sg"),
                    Downloads = null, // 该目录不提供下载量
                    Forks = ReadNullableInt(item, "fk"),
                    Verified = ReadString(item, "v").Equals("verified", StringComparison.OrdinalIgnoreCase),
                    Added = ReadString(item, "a"),
                    InstallSpec = ExtractInstallSpec(ReadString(item, "ic")),
                    FallbackInstallSpec = ExtractInstallSpec(ReadString(item, "igc")),
                });
            }

            return new PluginCatalog
            {
                Entries = entries,
                Categories = DshPluginOrgCategories,
                // 该目录顶层没有 updated 字段,不编造 —— 界面会退化成只显示拉取时间
                Updated = string.Empty,
                SourceUrl = sourceUrl,
                FetchedAtLocal = DateTime.Now,
            };
        }

        /// <summary>
        /// dsh-plugin.org 的分类中文名。数据文件里只有分类 id,中文名在上游
        /// <c>src/client/logic/constants.ts</c> 的 <c>CATEGORY_LABELS</c> 里,这里照抄同一份。
        /// </summary>
        private static readonly Dictionary<string, string> DshPluginOrgCategories = new(StringComparer.Ordinal)
        {
            ["interface"] = "界面与体验",
            ["session"] = "会话与消息",
            ["memory"] = "记忆与上下文",
            ["tools"] = "工具与能力",
            ["agent"] = "技能与智能体",
            ["workflow"] = "工作流与自动化",
            ["integration"] = "集成与连接",
            ["model"] = "模型与推理",
            ["dev"] = "开发与运维",
            ["knowledge"] = "数据与知识",
            ["fun"] = "娱乐",
        };


        /// <summary>
        /// 从目录给的 <c>dsh plugin --profile &lt;name&gt; add &lt;spec&gt;</c> 里取出 <c>&lt;spec&gt;</c>。
        /// 目录已经算好了首选来源(npm 包名优先,没有 npm 包时是 github:owner/repo)。
        /// </summary>
        private static string ExtractInstallSpec(string install)
        {
            if (install.Length == 0)
            {
                return string.Empty;
            }

            var match = InstallSpecRegex().Match(install);
            return match.Success ? match.Groups["spec"].Value.Trim() : install.Trim();
        }

        private static string ReadString(JsonElement element, string name)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
            {
                return string.Empty;
            }

            return value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
        }

        /// <summary>读嵌套字符串字段(如 description.zh)。</summary>
        private static string ReadString(JsonElement element, string name, string nested)
            => element.TryGetProperty(name, out var child) ? ReadString(child, nested) : string.Empty;

        private static int ReadInt(JsonElement element, string name)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
            {
                return 0;
            }

            return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : 0;
        }

        /// <summary>读可空整数(目录里没有该项时返回 null,界面据此决定要不要显示)。</summary>
        private static int? ReadNullableInt(JsonElement element, string name)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
            {
                return null;
            }

            return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;
        }

        /// <summary>
        /// 盘点插件:读 profile 清单 + 组合树 + 已安装版本。
        /// 只读操作,可反复调用(界面刷新、操作后回填都用它)。
        /// </summary>
        public async Task<PluginSnapshot> LoadAsync()
        {
            var warnings = new List<string>();
            var dependencies = new Dictionary<string, string>(StringComparer.Ordinal);
            var bundles = new List<string>();

            try
            {
                if (File.Exists(ManifestPath))
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(ManifestPath), ManifestReadOptions);
                    var root = document.RootElement;

                    if (root.TryGetProperty("dependencies", out var deps) && deps.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var property in deps.EnumerateObject())
                        {
                            dependencies[property.Name] = property.Value.GetString() ?? string.Empty;
                        }
                    }

                    if (root.TryGetProperty("dsh", out var dsh)
                        && dsh.TryGetProperty("profile", out var profile)
                        && profile.TryGetProperty("bundles", out var bundleList)
                        && bundleList.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in bundleList.EnumerateArray())
                        {
                            if (item.GetString() is { Length: > 0 } name)
                            {
                                bundles.Add(name);
                            }
                        }
                    }
                }
                else
                {
                    warnings.Add($"未找到 profile 清单 {ManifestPath}(尚未创建 profile?)");
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"读取 profile 清单失败: {ex.Message}");
            }

            var overrides = ReadOverrides();
            var pnpmAvailable = await CheckPnpmAvailableAsync();

            var (exitCode, dump, dumpError) = await DshService.Instance.RunDshCaptureAsync(
                $"--profile {ProfileName} --dump-config");
            if (exitCode != 0 || dump.Length == 0)
            {
                warnings.Add($"`dsh --profile {ProfileName} --dump-config` 未返回有效内容(退出代码 {exitCode}): "
                    + Truncate(dumpError, 200));
            }

            var composed = ParseCompositionDump(dump);
            var bundleSet = new HashSet<string>(bundles, StringComparer.Ordinal);

            // 组合树条目:自带插件(如 ui-schedule)也能在这里启停
            var allEntries = composed
                .Select(row => new PluginEntry
                {
                    Id = row.Id,
                    Name = row.Name,
                    Version = ResolveInstalledVersion(row.Name),
                    IsInstalled = dependencies.ContainsKey(row.Name),
                    IsBundle = bundleSet.Contains(row.Name),
                    IsActive = !row.Disabled,
                    InComposition = true,
                    HasOverride = overrides.ContainsKey(row.Id),
                })
                .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // dependencies 里没有出现在组合树上的包(普通依赖,不会成为 profile 层)
            var composedNames = new HashSet<string>(allEntries.Select(e => e.Name), StringComparer.Ordinal);
            var notComposed = dependencies.Keys
                .Where(name => !composedNames.Contains(name))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .Select(name => new PluginEntry
                {
                    Id = string.Empty,
                    Name = name,
                    Version = ResolveInstalledVersion(name),
                    IsInstalled = true,
                    IsBundle = bundleSet.Contains(name),
                    IsActive = true,
                    InComposition = false,
                })
                .ToList();

            return new PluginSnapshot
            {
                Installed = [.. allEntries.Where(e => e.IsInstalled), .. notComposed],
                AllEntries = allEntries,
                PnpmAvailable = pnpmAvailable,
                Warning = warnings.Count == 0 ? null : string.Join("\r\n", warnings),
            };
        }

        /// <summary>
        /// 安装插件。<paramref name="spec"/> 可以是包名、<c>@scope/name</c>、版本范围、
        /// tarball/本地路径或 git 地址(直接交给 pnpm add)。
        /// </summary>
        public async Task<bool> InstallAsync(string spec) => await this.InstallCoreAsync(spec) == 0;

        /// <summary>
        /// 安装插件的实现,返回 <c>dsh plugin</c> 的退出码(可能是 <see cref="RejectedExitCode"/>)。
        /// 把退出码交给调用方,是为了让目录安装能区分“确实装不上”(该回退到备选来源)
        /// 与“被并发互斥拒绝”(回退也只会被再拒一次)。
        /// </summary>
        private async Task<int> InstallCoreAsync(string spec)
        {
            spec = NormalizeSpec(spec);
            if (spec.Length == 0)
            {
                return -1;
            }

            var exitCode = await RunDshPluginAsync(
                $"add {PlatformProcess.Quote(spec)}",
                $"> dsh plugin --profile {ProfileName} add {spec}");

            if (exitCode == 0)
            {
                this.AppendSystemLog($"已安装插件 {spec}");
                return 0;
            }

            if (exitCode == RejectedExitCode)
            {
                // 拒绝原因已由 RunDshPluginAsync 写进日志
                return RejectedExitCode;
            }

            this.AppendSystemLog($"安装插件失败(退出代码 {exitCode}): {spec}");
            this.AppendHintForExitCode(exitCode);
            return exitCode;
        }

        /// <summary>
        /// 从策展目录安装:先按目录给的首选规格装(npm 包优先,GitHub-only 的是 <c>github:owner/repo</c>),
        /// 装不上时用目录给的备选规格回退:
        /// awesome-dsh-plugin 是作者预构建的 GitHub Release tarball,
        /// dsh-plugin.org 是它的 GitHub 源码通道(igc)。即上游的「npm → 预构建 tarball → 整仓源码」顺序。
        /// </summary>
        public async Task<bool> InstallCatalogEntryAsync(PluginCatalogEntry entry)
        {
            var spec = entry.EffectiveInstallSpec;
            if (spec.Length == 0)
            {
                this.AppendSystemLog($"{entry.Name}: 目录里没有可用的安装来源");
                return false;
            }

            this.AppendSystemLog($"从目录安装 {entry.Name}(来源 {spec})");
            var exitCode = await this.InstallCoreAsync(spec);
            if (exitCode == 0)
            {
                return true;
            }

            if (exitCode == RejectedExitCode)
            {
                // 被并发互斥拒绝:换备选来源重试同样会被拒绝,没必要再试
                return false;
            }

            var fallback = entry.FallbackInstallSpec;
            if (fallback.Length > 0 && !string.Equals(fallback, spec, StringComparison.Ordinal))
            {
                this.AppendSystemLog($"{entry.Name}: 首选项装不上,回退到目录给的备选来源");
                return await this.InstallCoreAsync(fallback) == 0;
            }

            return false;
        }

        /// <summary>卸载插件(仅限 profile dependencies 里的包)。</summary>
        public async Task<bool> UninstallAsync(string packageName)
        {
            if (packageName.Length == 0)
            {
                return false;
            }

            var exitCode = await RunDshPluginAsync(
                $"remove {PlatformProcess.Quote(packageName)}",
                $"> dsh plugin --profile {ProfileName} remove {packageName}");

            if (exitCode == 0)
            {
                this.AppendSystemLog($"已卸载插件 {packageName}");
                return true;
            }

            if (exitCode == RejectedExitCode)
            {
                // 拒绝原因已由 RunDshPluginAsync 写进日志
                return false;
            }

            this.AppendSystemLog($"卸载插件失败(退出代码 {exitCode}): {packageName}");
            this.AppendHintForExitCode(exitCode);
            return false;
        }

        /// <summary>
        /// 启用/禁用组合条目:往 cordis.patch.yml 的托管区块写一条 id 定向的 disabled 覆盖。
        /// 只改这一个文件,不动 dsh 自带的 bundle 文件。
        /// </summary>
        public bool SetEnabled(string id, bool enabled)
        {
            if (id.Length == 0)
            {
                return false;
            }

            try
            {
                var overrides = ReadOverrides();
                overrides[id] = !enabled;
                WriteOverrides(overrides);
                this.AppendSystemLog($"{(enabled ? "启用" : "禁用")}插件条目 {id}(已写入 {PatchFilePath})");
                return true;
            }
            catch (Exception ex)
            {
                this.AppendSystemLog($"写入启停覆盖失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>删除该条目的启停覆盖,回到 bundle 自带的默认状态。</summary>
        public bool ClearOverride(string id)
        {
            try
            {
                var overrides = ReadOverrides();
                if (!overrides.Remove(id))
                {
                    return true;
                }

                WriteOverrides(overrides);
                this.AppendSystemLog($"已恢复 {id} 的默认启停状态");
                return true;
            }
            catch (Exception ex)
            {
                this.AppendSystemLog($"恢复默认启停失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 批量启用/禁用。**只读一次、只写一次 <c>cordis.patch.yml</c>** ——
        /// 逐条调 <see cref="SetEnabled"/> 会把整个文件重写 N 次(152 条就是 152 次)。
        /// </summary>
        public bool SetEnabledBatch(IReadOnlyCollection<string> ids, bool enabled)
        {
            var targets = ids.Where(id => id.Length > 0).Distinct(StringComparer.Ordinal).ToList();
            if (targets.Count == 0)
            {
                return false;
            }

            try
            {
                var overrides = ReadOverrides();
                foreach (var id in targets)
                {
                    overrides[id] = !enabled;
                }

                WriteOverrides(overrides);
                this.AppendSystemLog($"批量{(enabled ? "启用" : "禁用")} {targets.Count} 个条目"
                    + $"(已写入 {PatchFilePath},重启或重载 dsh 后生效)");
                return true;
            }
            catch (Exception ex)
            {
                this.AppendSystemLog($"批量写启停覆盖失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>批量恢复默认:一次摘掉多个 id 的覆盖(同样是只写一次文件)。</summary>
        public bool ClearOverridesBatch(IReadOnlyCollection<string> ids)
        {
            var targets = ids.Where(id => id.Length > 0).Distinct(StringComparer.Ordinal).ToList();
            if (targets.Count == 0)
            {
                return false;
            }

            try
            {
                var overrides = ReadOverrides();
                var removed = targets.Count(id => overrides.Remove(id));
                WriteOverrides(overrides);
                this.AppendSystemLog($"已恢复 {removed} 个条目的默认启停状态");
                return true;
            }
            catch (Exception ex)
            {
                this.AppendSystemLog($"批量恢复默认失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 批量卸载:逐个交给 <c>dsh plugin remove</c>(逐个执行,不因为中途失败而放弃后面的),
        /// 返回成功的个数。调用方在全部结束后**刷新一次**即可,不必每条都刷。
        /// </summary>
        public async Task<int> UninstallBatchAsync(IReadOnlyCollection<string> packageNames)
        {
            var targets = packageNames
                .Where(name => name.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (targets.Count == 0)
            {
                return 0;
            }

            this.AppendSystemLog($"开始批量卸载 {targets.Count} 个插件: {string.Join(", ", targets)}");
            var succeeded = 0;
            foreach (var name in targets)
            {
                if (await this.UninstallAsync(name))
                {
                    succeeded++;
                }
            }

            this.AppendSystemLog($"批量卸载结束:成功 {succeeded} / {targets.Count}");
            return succeeded;
        }

        /// <summary>探测 pnpm 是否可用(安装/卸载都由 dsh plugin 转发给 pnpm)。</summary>
        public async Task<bool> CheckPnpmAvailableAsync()
        {
            var (stdout, _) = await RunCaptureAsync("pnpm --version");
            return VersionRegex().IsMatch(stdout);
        }

        /// <summary>打开 profile 目录,便于用户手工查看/编辑清单与补丁层。</summary>
        public void OpenProfileDirectory()
        {
            try
            {
                Directory.CreateDirectory(ProfileDirectory);
                var target = File.Exists(PatchFilePath) ? PatchFilePath : ProfileDirectory;
                Process.Start(PlatformProcess.CreateRevealInFileManagerStartInfo(target));
            }
            catch (Exception ex)
            {
                this.AppendSystemLog($"打开插件目录失败: {ex.Message}");
            }
        }

        /// <summary>拼接 dsh plugin 的完整参数(带引号),并在页面日志里回显。</summary>
        private async Task<int> RunDshPluginAsync(string pluginArguments, string echo)
        {
            // 同一时刻只允许一个插件变更操作:并发跑两个 pnpm 会互相覆盖 profile 的包清单
            // (列表行上的「安装」不受忙碌态禁用影响,连点两次就会撞上)
            if (!this._operationGate.Wait(0))
            {
                this.AppendLog("已有插件操作正在进行(安装/卸载),本次操作已忽略,请等它结束后重试。\r\n");
                return RejectedExitCode;
            }

            this._busy = true;
            StateChanged?.Invoke();
            try
            {
                this.AppendLog(echo);
                if (!await this.CheckPnpmAvailableAsync())
                {
                    this.AppendLog("pnpm 不可用:dsh plugin 会把参数转发给 profile 目录下的 pnpm。"
                        + "请先安装/修复 pnpm(例如 npm i -g pnpm@10),再重试。\r\n");
                }

                var exitCode = await DshService.Instance.RunDshStreamingAsync(
                    $"plugin --profile {ProfileName} {pluginArguments}",
                    line => this.AppendLog(line));
                this.AppendLog($"[退出代码 {exitCode}]");
                return exitCode;
            }
            finally
            {
                this._busy = false;
                this._operationGate.Release();
                StateChanged?.Invoke();
            }
        }

        private void AppendHintForExitCode(int exitCode)
        {
            if (exitCode == RejectedExitCode)
            {
                // 并发互斥拒绝:原因已经写在日志里,不要再补"git 来源"那类与本次无关的提示
                return;
            }

            if (exitCode == 127)
            {
                this.AppendLog("提示:未找到 pnpm。请先执行 npm install -g pnpm 后重试。\r\n");
            }
            else if (exitCode != 0)
            {
                this.AppendLog("提示:若为 git 来源的插件,其 prepare 脚本需要在 profile 的 pnpm-workspace.yaml 里允许构建"
                    + "(按上面 pnpm 打印的键加入 allowBuilds)后重试。\r\n");
            }
        }

        /// <summary>
        /// 规整用户输入的插件规格:
        /// dsh 会把 <c>.</c>/<c>../x</c> 这类相对路径按“调用者的工作目录”改写,而启动器的工作目录不确定,
        /// 因此本地路径一律转成绝对路径再交给 pnpm。
        /// </summary>
        private static string NormalizeSpec(string spec)
        {
            spec = spec.Trim();
            if (spec.Length == 0)
            {
                return string.Empty;
            }

            var prefix = string.Empty;
            var body = spec;
            foreach (var candidate in new[] { "file:", "link:" })
            {
                if (spec.StartsWith(candidate, StringComparison.OrdinalIgnoreCase))
                {
                    prefix = candidate;
                    body = spec[candidate.Length..];
                    break;
                }
            }

            if (body.StartsWith('.') || body.StartsWith("~/") || body.StartsWith("~\\"))
            {
                try
                {
                    if (body.StartsWith("~/") || body.StartsWith("~\\"))
                    {
                        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                        body = Path.Combine(home, body[2..]);
                    }

                    return prefix + Path.GetFullPath(body);
                }
                catch (Exception)
                {
                    // 路径非法时原样交给 pnpm,由它给出错误信息
                    return spec;
                }
            }

            return spec;
        }

        /// <summary>读已安装版本:profile 目录优先,其次是 profiles 根(hoisted 链接器会把包提升到那里)。</summary>
        private static string? ResolveInstalledVersion(string packageName)
        {
            if (packageName.Length == 0 || packageName.StartsWith('.') || Path.IsPathRooted(packageName))
            {
                return null;
            }

            foreach (var root in new[] { Path.Combine(ProfileDirectory, "node_modules"), Path.Combine(DshHomeDirectory, "profiles", "node_modules") })
            {
                try
                {
                    var manifest = Path.Combine(root, packageName.Replace('/', Path.DirectorySeparatorChar), "package.json");
                    if (!File.Exists(manifest))
                    {
                        continue;
                    }

                    using var document = JsonDocument.Parse(File.ReadAllText(manifest), ManifestReadOptions);
                    if (document.RootElement.TryGetProperty("version", out var version))
                    {
                        return version.GetString();
                    }
                }
                catch (Exception)
                {
                    // 读不到就显示"未知版本",不影响其他条目
                }
            }

            return null;
        }

        /// <summary>
        /// 解析 <c>dsh --profile &lt;name&gt; --dump-config</c> 的输出。
        /// 只取顶层行(<c>- id:</c> / <c>name:</c> / <c>disabled:</c>),
        /// <c>config:</c> 下面的内容(含 <c>!!js</c> 表达式)整段跳过——那里的同名字段不代表条目状态。
        /// 输出的 <c># == &lt;层&gt;</c> 注释行按层分组展示,同一 id 只会出现一次。
        /// </summary>
        private static List<(string Id, string Name, bool Disabled)> ParseCompositionDump(string dump)
        {
            var rows = new List<(string Id, string Name, bool Disabled)>();
            string? id = null;
            var name = string.Empty;
            var disabled = false;
            var inConfig = false;

            void Flush()
            {
                if (id is not null)
                {
                    rows.Add((id, name, disabled));
                }

                id = null;
                name = string.Empty;
                disabled = false;
            }

            foreach (var rawLine in dump.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r');
                if (line.Length == 0 || line.TrimStart().StartsWith('#'))
                {
                    continue;
                }

                if (line.StartsWith("- ", StringComparison.Ordinal))
                {
                    Flush();
                    inConfig = false;
                    id = line[2..].Trim() is var item && item.StartsWith("id:", StringComparison.Ordinal)
                        ? Unquote(item[3..].Trim())
                        : null;
                    continue;
                }

                if (id is null)
                {
                    continue;
                }

                var indent = line.Length - line.TrimStart().Length;
                var text = line.Trim();

                if (indent <= 2)
                {
                    if (text.StartsWith("name:", StringComparison.Ordinal))
                    {
                        name = Unquote(text[5..].Trim());
                    }
                    else if (text.StartsWith("disabled:", StringComparison.Ordinal))
                    {
                        disabled = text[9..].Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
                    }
                    else if (text.StartsWith("config:", StringComparison.Ordinal))
                    {
                        inConfig = true;
                    }
                }
                else if (inConfig)
                {
                    // config 段内部,忽略
                }
            }

            Flush();
            return rows;
        }

        private static string Unquote(string value)
            => value.Length >= 2 && value[0] == '\'' && value[^1] == '\''
                ? value[1..^1].Replace("''", "'", StringComparison.Ordinal)
                : value.Trim('"');

        /// <summary>读托管区块里的启停覆盖(id → disabled)。</summary>
        private Dictionary<string, bool> ReadOverrides()
        {
            var result = new Dictionary<string, bool>(StringComparer.Ordinal);
            try
            {
                if (!File.Exists(PatchFilePath))
                {
                    return result;
                }

                var lines = SplitLines(File.ReadAllText(PatchFilePath));
                var (begin, end) = FindManagedRange(lines);
                if (begin < 0)
                {
                    return result;
                }

                string? id = null;
                for (var i = begin + 1; i < end; i++)
                {
                    var text = lines[i].Trim();

                    if (text.StartsWith("- ", StringComparison.Ordinal))
                    {
                        var item = text[2..].Trim();
                        id = item.StartsWith("id:", StringComparison.Ordinal) ? Unquote(item[3..].Trim()) : null;
                    }
                    else if (id is not null && text.StartsWith("disabled:", StringComparison.Ordinal))
                    {
                        result[id] = text[9..].Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
                        id = null;
                    }
                }
            }
            catch (Exception ex)
            {
                this.AppendSystemLog($"读取启停覆盖失败: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// 重写托管区块(逐行处理,只用注释行作边界,不动用户自己写的补丁条目)。
        ///
        /// ⚠ 关键约束:<c>[]</c> 本身就是一份完整的 YAML 文档,后面再追加条目会直接解析失败
        /// (实测 "end of the stream or a document separator is expected")。
        /// 所以有覆盖时必须**删掉**那行空数组标记,而不是在它下面追加。
        /// </summary>
        private void WriteOverrides(Dictionary<string, bool> overrides)
        {
            var original = File.Exists(PatchFilePath) ? File.ReadAllText(PatchFilePath) : string.Empty;

            // 行尾风格跟着原文件走,避免把 LF 文件整体改成 CRLF
            var lineEnding = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var lines = SplitLines(original);

            // ① 先摘掉上一次写的托管区块(含两行标记)
            var (begin, end) = FindManagedRange(lines);
            if (begin >= 0)
            {
                lines.RemoveRange(begin, end - begin + 1);
            }

            // ② 去掉尾部空行,让后面追加的内容紧贴已有内容
            while (lines.Count > 0 && lines[^1].Trim().Length == 0)
            {
                lines.RemoveAt(lines.Count - 1);
            }

            var hasUserEntries = lines.Any(line => line.TrimStart().StartsWith("- ", StringComparison.Ordinal));

            // ③ 空数组标记本身就是一份完整的 YAML 文档:有覆盖时必须删掉它,
            //    否则在它后面追加条目会解析失败(见方法注释)
            if (!hasUserEntries)
            {
                lines.RemoveAll(line => line.Trim() == "[]");
            }

            if (overrides.Count == 0)
            {
                if (!hasUserEntries)
                {
                    lines.Add("[]");
                }
            }
            else
            {
                if (hasUserEntries)
                {
                    lines.Add(string.Empty); // 与用户自己写的条目之间留一个空行
                }

                lines.Add(ManagedBegin);
                foreach (var pair in overrides.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    lines.Add($"- id: {pair.Key}");
                    lines.Add($"  disabled: {(pair.Value ? "true" : "false")}");
                }

                lines.Add(ManagedEnd);
            }

            Directory.CreateDirectory(ProfileDirectory);
            File.WriteAllText(PatchFilePath, string.Join(lineEnding, lines) + lineEnding);
        }

        /// <summary>按行拆分(先把 CRLF 归一为 LF,写出时再按原风格拼接)。</summary>
        private static List<string> SplitLines(string text)
            => text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();

        /// <summary>定位托管区块的行范围(含两行标记);没有则返回 (-1, -1)。</summary>
        private static (int Begin, int End) FindManagedRange(List<string> lines)
        {
            var begin = lines.FindIndex(line => line.Contains(ManagedBegin, StringComparison.Ordinal));
            if (begin < 0)
            {
                return (-1, -1);
            }

            var end = lines.FindIndex(begin, line => line.Contains(ManagedEnd, StringComparison.Ordinal));
            return (begin, end < 0 ? lines.Count - 1 : end);
        }

        private static string Truncate(string value, int maxLength)
            => value.Length <= maxLength ? value : value[..maxLength] + "…";

        /// <summary>执行一条命令并捕获 stdout/stderr(用于 pnpm 探测)。</summary>
        private static async Task<(string Stdout, string Stderr)> RunCaptureAsync(string command)
        {
            try
            {
                // pnpm 是 Node 程序、写 UTF-8;cmd 自身消息是 OEM 代码页 —— 原样收下再逐行判定
                var startInfo = PlatformProcess.CreateShellStartInfo(
                    command, redirectOutput: true, rawByteOutput: true);

                // npm 源 / 代理:探测包管理器时也保持一致(否则“探测失败但手工命令可用”的报错会莫名奇妙)
                ChildEnvironment.Apply(startInfo);

                using var process = new Process { StartInfo = startInfo };
                process.Start();

                // 必须先 Start 再读 StandardOutput,否则抛
                // "StandardOut has not been redirected or the process hasn't started yet"
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                return (PlatformProcess.DecodeChildOutputText(await stdout),
                    PlatformProcess.DecodeChildOutputText(await stderr));
            }
            catch (Exception ex)
            {
                return (string.Empty, ex.Message);
            }
        }

        [GeneratedRegex(@"\d+\.\d+")]
        private static partial Regex VersionRegex();

        /// <summary>从目录的 install 字段里取规格:<c>dsh plugin --profile web add &lt;spec&gt;</c>。</summary>
        [GeneratedRegex(@"\badd\s+(?<spec>\S.*?)\s*$")]
        private static partial Regex InstallSpecRegex();
    }
}
