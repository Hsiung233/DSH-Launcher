using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace DSH_Launcher.Models
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

        private bool _isPendingUninstall;

        /// <summary>
        /// 卸载按钮是否处于"再点一次确认"的状态(仅界面状态,不影响 profile)。
        /// <para>
        /// ⚠ 必须存在**数据对象**上,而不是像早期实现那样改 <c>Button.Content</c>:
        /// 列表是虚拟化的、容器会被回收重用,而模板里的 <c>Content</c> 是字面量(不是绑定),
        /// 容器被另一行复用时会把"确认卸载"一起带过去 —— 用户滚动后看到别的插件也处于待确认状态。
        /// 这与 <see cref="IsSelected"/> 的理由相同。
        /// </para>
        /// </summary>
        public bool IsPendingUninstall
        {
            get => this._isPendingUninstall;
            set
            {
                if (this._isPendingUninstall == value)
                {
                    return;
                }

                this._isPendingUninstall = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.IsPendingUninstall)));
                // 按钮文案是另一个绑定,必须一起通知,否则状态变了文字不变
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.UninstallText)));
            }
        }

        /// <summary>卸载按钮的文案:待确认时变成「确认卸载」。</summary>
        public string UninstallText => this._isPendingUninstall ? "确认卸载" : "卸载";

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

        /// <summary>
        /// 服务正在运行时安装的插件:运行中的 dsh 要重启后才会装载它。
        /// 注意不能用"不在组合树"推断 —— <c>--dump-config</c> 是新进程读磁盘,
        /// 安装后立刻刷新就会让该条目"看起来已在组合里",但它对运行实例还没生效。
        /// 由 <see cref="Services.PluginService"/> 在安装当刻对账 dependencies 前后差集得出,
        /// dsh 停止或重启后自动清空。
        /// </summary>
        public bool AwaitingRestart { get; init; }

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

        /// <summary>状态说明:已启用 / 已禁用 / 未参与组合;等待重启生效时追加标注。</summary>
        public string StateText =>
            (this.InComposition ? (this.IsActive ? "已启用" : "已禁用") : "未参与组合")
            + (this.AwaitingRestart ? " · 重启后生效" : string.Empty);

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
}
