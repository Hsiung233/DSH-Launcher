using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DSH_Launcher.Models;
using DSH_Launcher.Services.Plugins;

namespace DSH_Launcher.Services
{
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

        private static readonly JsonDocumentOptions ManifestReadOptions = new()
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        };

        /// <summary>
        /// 插件页日志缓冲区。日志会被多个线程写:RunDshStreamingAsync 的 stdout/stderr 回调(线程池)
        /// 与 UI 线程的操作日志;读侧还有 LogText 与 ClearLog —— 线程安全与长度裁剪都由
        /// <see cref="LogBuffer"/> 负责。上限理由同 <see cref="DshService"/>:pnpm/dsh 报错时会刷屏,
        /// 而面板是"整段文本 + 每行追加全量重排"的实现,不限长会把 UI 线程钉死在文本排版里。
        /// </summary>
        private readonly LogBuffer _log = new(LogBuffer.DefaultMaxChars);

        private volatile bool _busy;

        /// <summary>pnpm 可用性探测结果(null = 还没探过);理由见 <see cref="CheckPnpmAvailableAsync"/>。</summary>
        private bool? _pnpmAvailable;

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

        public string LogText => this._log.ToString();

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

            // 超长时由 LogBuffer 从头部按整行裁剪(见 LogBuffer.DefaultMaxChars 的说明)
            this._log.Append(line);

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
            this._log.Clear();

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
        /// <para>
        /// 用 <see cref="ConcurrentDictionary{TKey,TValue}"/> 而不是普通字典:读写在 await 之后发生,
        /// 目前靠"调用方都在 UI 线程的同步上下文上"这一约定才安全;换成并发容器后,
        /// 将来谁把 <c>LoadCatalogAsync</c> 挪到后台线程(或加 <c>ConfigureAwait(false)</c>)都不会踩到。
        /// </para>
        /// </summary>
        private readonly ConcurrentDictionary<string, PluginCatalog> _catalogCache = new(StringComparer.Ordinal);

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

                var catalog = PluginCatalogReader.Parse(document.RootElement, url);

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
        /// 盘点插件:读 profile 清单 + 组合树 + 已安装版本。
        /// 只读操作,可反复调用(界面刷新、操作后回填都用它)。
        /// </summary>
        /// <param name="forcePnpmProbe">
        /// 是否重新探测 pnpm(见 <see cref="CheckPnpmAvailableAsync"/>)。界面上的「刷新」按钮传 true ——
        /// 用户按提示修好 pnpm 后点刷新,必须看到状态变化。
        /// </param>
        public async Task<PluginSnapshot> LoadAsync(bool forcePnpmProbe = false)
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
            var pnpmAvailable = await CheckPnpmAvailableAsync(forcePnpmProbe);

            var (exitCode, dump, dumpError) = await DshCli.RunCaptureAsync(
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

        /// <summary>
        /// 探测 pnpm 是否可用(安装/卸载都由 dsh plugin 转发给 pnpm)。
        /// <para>
        /// ⚠ 结果按**本次运行**缓存:探测要起一个子进程(类 Unix 上还是登录 shell,要加载 profile),
        /// 而列表刷新发生在每次进页面、每次批量操作之后 —— 每次都探一遍纯属浪费。
        /// 缓存由 <paramref name="forceRefresh"/>(界面的「刷新」按钮)或 <see cref="InvalidatePnpmProbe"/>
        /// (命令明确报 127 = 找不到 pnpm)打破。
        /// </para>
        /// </summary>
        public async Task<bool> CheckPnpmAvailableAsync(bool forceRefresh = false)
        {
            if (!forceRefresh && this._pnpmAvailable is bool cached)
            {
                return cached;
            }

            try
            {
                var result = await ChildProcessRunner.CaptureAsync("pnpm --version");
                this._pnpmAvailable = VersionRegex().IsMatch(result.Stdout);
            }
            catch (Exception)
            {
                // 探测本身失败(命令不可用等)按"不可用"处理
                this._pnpmAvailable = false;
            }

            return this._pnpmAvailable.Value;
        }

        /// <summary>丢弃 pnpm 探测结果,下一次刷新会重新探测。</summary>
        private void InvalidatePnpmProbe()
        {
            this._pnpmAvailable = null;
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

                var exitCode = await DshCli.RunStreamingAsync(
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
                // 命令明确报"找不到 pnpm":探测结果作废,下一次刷新会重探(用户可能刚装上)
                this.InvalidatePnpmProbe();
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
        /// <remarks>internal 而非 private:被单元测试覆盖(见 DSH Launcher.Tests)。</remarks>
        internal static string NormalizeSpec(string spec)
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
        /// <remarks>internal 而非 private:这段"YAML 缩进知识"被单元测试覆盖(见 DSH Launcher.Tests)。</remarks>
        internal static List<(string Id, string Name, bool Disabled)> ParseCompositionDump(string dump)
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

        /// <summary>
        /// 读托管区块里的启停覆盖(id → disabled)。文件格式细节见 <see cref="PluginPatchFile"/>。
        /// <para>
        /// 读失败时在这里就吞掉并上报(而不是向上抛):调用方既有“读-改-写”的操作,
        /// 也有盘点时的只读调用(盘点不在 try 里,抛出去会让整次刷新失败)。
        /// </para>
        /// </summary>
        private Dictionary<string, bool> ReadOverrides()
        {
            try
            {
                return PluginPatchFile.Read(PatchFilePath);
            }
            catch (Exception ex)
            {
                this.AppendSystemLog($"读取启停覆盖失败: {ex.Message}");
                return new Dictionary<string, bool>(StringComparer.Ordinal);
            }
        }

        /// <summary>重写托管区块(格式细节与 <c>[]</c> 那个坑见 <see cref="PluginPatchFile.Write"/>)。</summary>
        private void WriteOverrides(Dictionary<string, bool> overrides)
            => PluginPatchFile.Write(PatchFilePath, overrides);

        private static string Truncate(string value, int maxLength)
            => value.Length <= maxLength ? value : value[..maxLength] + "…";

        [GeneratedRegex(@"\d+\.\d+")]
        private static partial Regex VersionRegex();
    }
}
