using System;
using System.Threading.Tasks;
using DSH_Launcher.Models;

namespace DSH_Launcher.Views.Shared
{
    /// <summary>一次"确保目录已加载"的结果。</summary>
    internal enum CatalogLoadOutcome
    {
        /// <summary>命中缓存,立即显示(没有发起新的拉取)。</summary>
        DisplayedFromCache,

        /// <summary>同一地址已经在拉取,本次不重复发起。</summary>
        AlreadyLoading,

        /// <summary>发起并成功。</summary>
        Loaded,

        /// <summary>发起并失败(原因见 <see cref="CatalogLoadResult.Error"/>)。</summary>
        Failed,

        /// <summary>发起了,但期间用户又切了来源 → 结果被丢弃,不覆盖更新的那份。</summary>
        Superseded,
    }

    /// <summary>一次"确保目录已加载"的结果。</summary>
    /// <param name="Outcome">结果类型。</param>
    /// <param name="Catalog">要显示的目录(仅 DisplayedFromCache / Loaded 有值)。</param>
    /// <param name="Error">失败原因(仅 Failed 有值)。</param>
    internal readonly record struct CatalogLoadResult(CatalogLoadOutcome Outcome, PluginCatalog? Catalog, string? Error);

    /// <summary>
    /// 插件目录的加载状态机:**缓存命中、同地址去重、世代号(防慢的旧来源覆盖快的新来源)**
    /// 这三件事集中在这里,页面只负责把结果显示出来。
    /// <para>
    /// 为什么值得单独一个类:目录拉取要 0.1-8 秒,用户完全可能在这个窗口里再切来源;
    /// 没有世代号就会出现"列表显示 A 而下拉已经是 B"。这段逻辑全是并发时序,靠肉眼看界面
    /// 几乎测不出来,抽出来之后可以用假的加载函数把每种时序固定下来(见 DSH Launcher.Tests)。
    /// </para>
    /// <para>
    /// 三个依赖都用委托注入(解析地址 / 查缓存 / 真拉取),因此本类不依赖界面,也不依赖服务单例。
    /// </para>
    /// </summary>
    internal sealed class CatalogLoadController
    {
        private readonly Func<string> _resolveUrl;
        private readonly Func<string, PluginCatalog?> _getCached;
        private readonly Func<bool, Task<PluginCatalog>> _load;

        /// <summary>目录请求的世代号:每次发起新请求(或命中缓存直接显示)都自增,回调里过期则丢弃。</summary>
        private int _requestId;

        /// <summary>正在拉取的地址;null = 当前没有在拉的请求(用于忙碌状态与同地址去重)。</summary>
        private string? _loadingUrl;

        /// <param name="resolveUrl">当前设置对应的目录地址。</param>
        /// <param name="getCached">按地址取已缓存目录(服务层持有缓存)。</param>
        /// <param name="load">真拉取(forceReload=true 绕过缓存)。</param>
        public CatalogLoadController(
            Func<string> resolveUrl,
            Func<string, PluginCatalog?> getCached,
            Func<bool, Task<PluginCatalog>> load)
        {
            this._resolveUrl = resolveUrl;
            this._getCached = getCached;
            this._load = load;
        }

        /// <summary>当前应显示的目录(未加载过时为 null)。</summary>
        public PluginCatalog? Catalog { get; private set; }

        /// <summary>是否有拉取在进行(界面的进度环/按钮可用性依赖它)。</summary>
        public bool IsLoading => this._loadingUrl is not null;

        /// <summary>最后一次请求的地址(失败提示里要显示它)。</summary>
        public string? LastRequestedUrl { get; private set; }

        /// <summary>
        /// 确保目录可用。命中缓存或同地址已在拉取时不会发起新请求。
        /// </summary>
        /// <param name="forceReload">true(界面「刷新目录」)时绕过缓存重新拉。</param>
        /// <param name="onStarted">真正发起拉取时回调一次(界面据此显示"正在读取…"并刷新忙碌态)。</param>
        public async Task<CatalogLoadResult> EnsureAsync(bool forceReload = false, Action? onStarted = null)
        {
            var url = this._resolveUrl();
            this.LastRequestedUrl = url;

            // ① 命中缓存:立即显示。
            //    这里也要自增世代号 —— 否则"A→B→A"时,A 命中缓存秒显,
            //    随后在飞的 B 请求完成(号还等于当前号)会把列表改成 B。
            if (!forceReload && this._getCached(url) is { } cached)
            {
                this._requestId++;
                this._loadingUrl = null;
                this.Catalog = cached;
                return new CatalogLoadResult(CatalogLoadOutcome.DisplayedFromCache, cached, null);
            }

            // ② 同一地址已在拉取且不是强制刷新 → 不重复发起
            if (!forceReload && string.Equals(this._loadingUrl, url, StringComparison.Ordinal))
            {
                return new CatalogLoadResult(CatalogLoadOutcome.AlreadyLoading, null, null);
            }

            var requestId = ++this._requestId;
            this._loadingUrl = url;
            onStarted?.Invoke();

            try
            {
                var catalog = await this._load(forceReload).ConfigureAwait(true);
                if (requestId != this._requestId)
                {
                    // 期间用户又切了来源 / 又点过刷新 → 丢弃本次结果(服务层已缓存,切回来即命中)
                    return new CatalogLoadResult(CatalogLoadOutcome.Superseded, null, null);
                }

                this.Catalog = catalog;
                return new CatalogLoadResult(CatalogLoadOutcome.Loaded, catalog, null);
            }
            catch (Exception ex)
            {
                if (requestId != this._requestId)
                {
                    return new CatalogLoadResult(CatalogLoadOutcome.Superseded, null, null);
                }

                // 上游刻意不拿旧数据冒充答案:这里也不回退缓存,直接把原因交给调用方显示
                return new CatalogLoadResult(CatalogLoadOutcome.Failed, null, ex.Message);
            }
            finally
            {
                // 只有还是最新那次请求才收尾;被顶替的请求不动状态(状态已归新请求所有)
                if (requestId == this._requestId)
                {
                    this._loadingUrl = null;
                }
            }
        }
    }
}
