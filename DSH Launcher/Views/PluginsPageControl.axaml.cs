using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DSH_Launcher.Services;

namespace DSH_Launcher.Views
{
    /// <summary>
    /// 插件页:① 管理已安装的插件(启用/禁用/卸载)② 从社区策展目录安装新插件。
    /// 插件的真实载体是 dsh 的 profile(&lt;DSH_HOME&gt;/profiles/web),本页只是它的可视化与常用操作入口。
    /// 插件来源是策展目录 awesome-dsh-plugin(即社区市场 dsh-market 用的那份数据),不是 npm registry。
    /// </summary>
    public partial class PluginsPageControl : UserControl
    {
        private readonly PluginService _plugins = PluginService.Instance;

        private PluginSnapshot? _snapshot;

        /// <summary>本次会话已拉到的插件目录(为空 = 还没拉过)。</summary>
        private PluginCatalog? _catalog;

        /// <summary>正在拉取目录的地址;null = 当前没有在拉的请求(用于忙碌状态与同地址去重)。</summary>
        private string? _catalogLoadingUrl;

        /// <summary>
        /// 目录请求的世代号。
        /// <para>
        /// ⚠ 为什么必须有它:目录拉取要 0.1-8 秒,用户完全可能在这个窗口里再切来源。
        /// 没有它就会出现“慢的旧来源最后落地”,列表显示 A 而下拉已经是 B。
        /// 每次发起新请求(或命中缓存直接显示)都自增,回调里发现自己的号过期就丢弃结果。
        /// 旧结果虽然不显示,但已经被服务层缓存了,下次切回来是瞬时的。
        /// </para>
        /// </summary>
        private int _catalogRequestId;

        /// <summary>回填下拉框/文本框或重建分类列表时置位,避免把回填当成用户操作。</summary>
        private bool _initializing;

        /// <summary>
        /// 页面已就绪(首次进页面、设置已回填完)。
        /// ⚠ 必须有它:Avalonia 的 ComboBox 会在构造时**自动选中第 0 项**,
        /// 那个 SelectionChanged 发生在 <see cref="OnPageAttached"/> 之前,`_initializing` 还没来得及置位,
        /// 结果会被当成用户操作把设置写回去(实测把用户的 dsh-plugin.org 静默改成 Official)。
        /// </summary>
        private bool _pageReady;

        /// <summary>正在读取插件清单(与服务的忙碌状态共同决定进度环与按钮可用性)。</summary>
        private bool _loading;

        /// <summary>右列「全部组合条目」当前实际显示的条目(受筛选框影响),批量选择以它为准。</summary>
        private IReadOnlyList<PluginEntry> _entriesView = [];

        /// <summary>已订阅选中态变化的条目(每次刷新重建,用于退订)。</summary>
        private readonly List<PluginEntry> _observedEntries = [];

        /// <summary>卸载的二次确认状态(点击「卸载」→ 变成「确认卸载」,5 秒内再点才真的卸载)。</summary>
        private string? _pendingUninstall;

        private bool _copyFeedbackBusy;

        public PluginsPageControl()
        {
            InitializeComponent();
            AttachedToVisualTree += this.OnPageAttached;

            // Esc 关闭输出浮窗。用**隧道**阶段先拿到并标记已处理:
            // 本页之外(FluentAvalonia 的 FANavigationView)也用了 Esc,不能让它抢走。
            this.AddHandler(KeyDownEvent, this.OnPagePreviewKeyDown, RoutingStrategies.Tunnel);

            this._plugins.LogAppended += this.OnPluginLogAppended;
            this._plugins.LogsCleared += this.OnPluginLogsCleared;
            this._plugins.StateChanged += this.OnPluginStateChanged;
        }

        /// <summary>
        /// 浮窗开着时按 Esc 关掉它,并阻止事件继续传播(否则会冒泡到导航控件)。
        /// 浮窗没开时**不处理**,保持应用原有的 Esc 行为不变。
        /// </summary>
        private void OnPagePreviewKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape || !this.PluginLogFlyout.IsVisible)
            {
                return;
            }

            this.SetPluginLogVisible(false);
            e.Handled = true;
        }

        /// <summary>进入页面时回填设置并后台读取插件清单(不阻塞首屏)。
        /// 插件目录不在这里拉(约 3MB):等切到「安装新插件」页签时才拉。</summary>
        private void OnPageAttached(object? sender, EventArgs e)
        {
            this._initializing = true;
            try
            {
                this.CatalogSourceCombo.SelectedIndex = (int)SettingsService.Instance.Settings.PluginCatalog;
            }
            finally
            {
                this._initializing = false;
            }

            // 默认停在「已安装」;不写在 XAML 里是因为 SelectedIndex 会在 InitializeComponent 期间
            // 触发 SelectionChanged,那时 InstallView/InstalledView 字段还没赋值。
            // 只在首次进入时赋默认值,这样同一次运行内切页再回来还记得上次的视图。
            if (this.PluginsViewList.SelectedIndex < 0)
            {
                this.PluginsViewList.SelectedIndex = 0;
            }

            this.ApplyView();
            _ = this.RefreshAsync();

            // 首次进入时若已经停在安装视图(比如用户上次就在这),也要把目录拉起来
            if (this.PluginsViewList.SelectedIndex == InstallViewIndex)
            {
                _ = this.EnsureCatalogAsync();
            }

            // 回填完毕:之后的 SelectionChanged 才算用户操作(见 _pageReady 注释)
            this._pageReady = true;
        }

        /// <summary>重新读取 profile 清单与组合树,刷新全部列表。</summary>
        private async Task RefreshAsync()
        {
            if (this._loading)
            {
                return;
            }

            this._loading = true;
            this.UpdateBusy();
            try
            {
                var snapshot = await PluginService.Instance.LoadAsync();
                this._snapshot = snapshot;
                this.ApplySnapshot(snapshot);
            }
            catch (Exception ex)
            {
                this._plugins.AppendLog($"[读取插件清单失败] {ex.Message}");
            }
            finally
            {
                this._loading = false;
                this.UpdateBusy();
            }
        }

        private void ApplySnapshot(PluginSnapshot snapshot)
        {
            // 选中态存在 PluginEntry 上,刷新后是新对象 —— 先把旧的监听退掉并重新监听新的
            this.ObserveSelection(snapshot);

            // ① 已安装的插件
            this.InstalledPluginsList.ItemsSource = snapshot.Installed;
            this.InstalledCountText.Text = snapshot.Installed.Count == 0 ? string.Empty : $"{snapshot.Installed.Count} 个";
            this.InstalledEmptyPanel.IsVisible = snapshot.Installed.Count == 0;

            // ② 全部组合条目(按筛选框过滤)
            this.AllEntriesCountText.Text = $"共 {snapshot.AllEntries.Count} 条";
            this.ApplyEntryFilter();

            // ③ pnpm 状态:安装/卸载都由 dsh plugin 转发给它,缺失时只提示、不禁用页面(启停不依赖 pnpm)
            this.PnpmBadge.IsVisible = !snapshot.PnpmAvailable;
            if (!snapshot.PnpmAvailable)
            {
                ToolTip.SetTip(this.PnpmBadge,
                    "未检测到可用的 pnpm:安装/卸载插件会经 dsh plugin 转发给 pnpm。"
                    + "可执行 npm install -g pnpm@10 修复后点「刷新」。");
            }

            var summary = $"profile “{PluginService.ProfileName}” · 已安装 {snapshot.Installed.Count} 个插件"
                + $" · 组合条目 {snapshot.AllEntries.Count} 条 · {PluginService.ProfileDirectory}";
            var notes = new List<string>();
            if (!snapshot.PnpmAvailable)
            {
                notes.Add("⚠ 未检测到可用的 pnpm:安装/卸载插件不可用(启用/禁用不依赖 pnpm)。"
                    + "可执行 npm install -g pnpm@10 修复后点「刷新」。");
            }

            if (snapshot.Warning is not null)
            {
                notes.Add("⚠ " + snapshot.Warning);
            }

            this.PluginsSubText.Text = notes.Count == 0
                ? summary
                : summary + Environment.NewLine + string.Join(Environment.NewLine, notes);
        }

        /// <summary>按筛选框内容过滤「全部组合条目」(包名或条目 id 的子串匹配)。</summary>
        private void ApplyEntryFilter()
        {
            var all = this._snapshot?.AllEntries ?? (IReadOnlyList<PluginEntry>)[];
            var filter = this.EntryFilterBox.Text?.Trim() ?? string.Empty;

            var view = filter.Length == 0
                ? all
                : all.Where(entry =>
                        entry.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                        || entry.Id.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    .ToList();

            // 记下当前显示的这批:批量选择/全选都以“看得见的”为准
            this._entriesView = view;
            this.AllEntriesList.ItemsSource = view;
            this.UpdateBatchBar();
        }

        /// <summary>
        /// 监听所有条目的选中态变化,以便实时刷新批量操作栏。
        /// 每次刷新会重建条目对象,所以先退订上一批再订新的(每次全量重建,数量只有百级,开销可忽略)。
        /// </summary>
        private void ObserveSelection(PluginSnapshot snapshot)
        {
            foreach (var entry in this._observedEntries)
            {
                entry.PropertyChanged -= this.OnEntryPropertyChanged;
            }

            this._observedEntries.Clear();

            // 两份列表会共享同一批实例(左列是从右列里筛出来的),用引用去重避免重复订阅
            var seen = new HashSet<PluginEntry>();
            foreach (var entry in snapshot.Installed.Concat(snapshot.AllEntries))
            {
                if (seen.Add(entry))
                {
                    this._observedEntries.Add(entry);
                }
            }

            foreach (var entry in this._observedEntries)
            {
                entry.PropertyChanged += this.OnEntryPropertyChanged;
            }
        }

        private void OnEntryPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(PluginEntry.IsSelected))
            {
                this.UpdateBatchBar();
            }
        }

        /// <summary>当前两列里可见的全部条目(左列已安装 + 右列筛选后的组合条目),去重。</summary>
        private List<PluginEntry> VisibleEntries()
        {
            var seen = new HashSet<PluginEntry>();
            var result = new List<PluginEntry>();
            foreach (var entry in (this._snapshot?.Installed ?? []).Concat(this._entriesView))
            {
                if (seen.Add(entry))
                {
                    result.Add(entry);
                }
            }

            return result;
        }

        /// <summary>
        /// 刷新批量操作栏:计数、按能力启用/禁用按钮、同步「全选」的勾选态。
        /// 每次勾选都重扫一遍(百级数据,很便宜),不维护增量计数 —— 增量计数容易跟筛选/刷新脱节。
        /// </summary>
        private void UpdateBatchBar()
        {
            var visible = this.VisibleEntries();
            var selected = visible.Where(entry => entry.IsSelected).ToList();

            var toggleable = selected.Count(entry => entry.CanToggle);
            var resettable = selected.Count(entry => entry.HasOverride);
            var uninstallable = selected.Count(entry => entry.CanUninstall);

            this.BatchCountText.Text = selected.Count == 0
                ? "未选择任何条目"
                : $"已选 {selected.Count} 项 · 可启停 {toggleable} · 可恢复 {resettable} · 可卸载 {uninstallable}";

            this.BatchEnableButton.IsEnabled = toggleable > 0;
            this.BatchDisableButton.IsEnabled = toggleable > 0;
            this.BatchResetButton.IsEnabled = resettable > 0;
            this.BatchUninstallButton.IsEnabled = uninstallable > 0;

            ToolTip.SetTip(this.BatchEnableButton, toggleable == 0
                ? "所选条目里没有可启停的(需出现在组合树里)"
                : $"对所选中的 {toggleable} 个条目写入 disabled: false");
            ToolTip.SetTip(this.BatchDisableButton, toggleable == 0
                ? "所选条目里没有可启停的(需出现在组合树里)"
                : $"对所选中的 {toggleable} 个条目写入 disabled: true");
            ToolTip.SetTip(this.BatchResetButton, resettable == 0
                ? "所选条目里没有启动器写入的启停覆盖"
                : $"删除所选中的 {resettable} 个条目的启停覆盖");
            ToolTip.SetTip(this.BatchUninstallButton, uninstallable == 0
                ? "所选条目里没有可卸载的(只有已安装到 profile 的包能卸载)"
                : $"从 profile 移除所选中的 {uninstallable} 个包");

            // 勾选态同步到「全选」;用 Click 而不是 Checked/Unchecked,避免这里的赋值又触发一次全选
            this.SelectAllBox.IsChecked = visible.Count > 0 && selected.Count == visible.Count;
        }

        /// <summary>全选 / 取消全选:作用于当前列表(左右两列已可见的条目)。</summary>
        private void OnSelectAllClick(object? sender, RoutedEventArgs e)
        {
            var select = this.SelectAllBox.IsChecked == true;
            foreach (var entry in this.VisibleEntries())
            {
                entry.IsSelected = select;
            }

            this.UpdateBatchBar();
        }

        private async void OnBatchEnableClick(object? sender, RoutedEventArgs e) => await this.ApplyBatchToggleAsync(true);

        private async void OnBatchDisableClick(object? sender, RoutedEventArgs e) => await this.ApplyBatchToggleAsync(false);

        /// <summary>批量启用/禁用:服务层一次写完 cordis.patch.yml,最后只刷新一次列表。</summary>
        private async Task ApplyBatchToggleAsync(bool enabled)
        {
            var ids = this.VisibleEntries()
                .Where(entry => entry.IsSelected && entry.CanToggle)
                .Select(entry => entry.Id)
                .ToList();

            if (ids.Count == 0)
            {
                return;
            }

            this._plugins.SetEnabledBatch(ids, enabled);
            await this.RefreshAsync();
        }

        private async void OnBatchResetClick(object? sender, RoutedEventArgs e)
        {
            var ids = this.VisibleEntries()
                .Where(entry => entry.IsSelected && entry.HasOverride)
                .Select(entry => entry.Id)
                .ToList();

            if (ids.Count == 0)
            {
                return;
            }

            this._plugins.ClearOverridesBatch(ids);
            await this.RefreshAsync();
        }

        /// <summary>
        /// 批量卸载:破坏性操作,所以用对话框列出**具体要卸载的包**再确认
        /// (逐条卸载那套“再点一次确认”不适合一次 N 个的场景)。
        /// </summary>
        private async void OnBatchUninstallClick(object? sender, RoutedEventArgs e)
        {
            var names = this.VisibleEntries()
                .Where(entry => entry.IsSelected && entry.CanUninstall)
                .Select(entry => entry.Name)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (names.Count == 0)
            {
                return;
            }

            var confirmed = await this.ConfirmBatchUninstallAsync(names);
            if (!confirmed)
            {
                return;
            }

            await this._plugins.UninstallBatchAsync(names);
            await this.RefreshAsync();
        }

        /// <summary>弹出确认对话框,列出即将卸载的包;返回是否确认。</summary>
        private async Task<bool> ConfirmBatchUninstallAsync(IReadOnlyList<string> names)
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is not Window owner)
            {
                return false;
            }

            var dialog = new FluentAvalonia.UI.Controls.FAContentDialog
            {
                Title = $"卸载 {names.Count} 个插件?",
                Content = new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "会从 profile 的 dependencies 里移除下面这些包(不可撤销):",
                            TextWrapping = TextWrapping.Wrap,
                        },
                        new ScrollViewer
                        {
                            MaxHeight = 200,
                            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                            Content = new SelectableTextBlock
                            {
                                Text = string.Join(Environment.NewLine, names),
                                TextWrapping = TextWrapping.Wrap,
                                FontFamily = new FontFamily("Consolas"),
                                FontSize = 12,
                            },
                        },
                    },
                },
                PrimaryButtonText = $"卸载 {names.Count} 个",
                CloseButtonText = "取消",
                DefaultButton = FluentAvalonia.UI.Controls.FAContentDialogButton.None,
            };

            var result = await dialog.ShowAsync(owner);
            return result == FluentAvalonia.UI.Controls.FAContentDialogResult.Primary;
        }

        private void UpdateBusy()
        {
            var busy = this._loading || this._catalogLoadingUrl is not null || this._plugins.IsBusy;
            this.PluginsProgress.IsActive = busy;
            this.RefreshPluginsButton.IsEnabled = !busy;
            this.RevealProfileButton.IsEnabled = !busy;
            this.RefreshCatalogButton.IsEnabled = !busy;
            this.InstallPluginButton.IsEnabled = !busy;
        }

        /// <summary>空态里的「去安装新插件」:切到第 2 个视图(0=已安装,1=安装新插件)。</summary>
        private void OnGoToInstallTabClick(object? sender, RoutedEventArgs e)
            => this.PluginsViewList.SelectedIndex = InstallViewIndex;

        /// <summary>启用/禁用条目:「禁用」写入 disabled 覆盖,已是禁用则写回启用。</summary>
        private async void OnToggleEntryClick(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button { DataContext: PluginEntry entry })
            {
                return;
            }

            var enable = !entry.IsActive;
            if (!this._plugins.SetEnabled(entry.Id, enable))
            {
                return;
            }

            this._plugins.AppendLog($"  → {entry.Id}: 已写入 disabled: {(enable ? "false" : "true")}"
                + ",重启或重载 dsh 后生效");
            await this.RefreshAsync();
        }

        /// <summary>删除启动器写入的启停覆盖,回到插件自带的默认状态。</summary>
        private async void OnClearOverrideClick(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button { DataContext: PluginEntry entry })
            {
                return;
            }

            this._plugins.ClearOverride(entry.Id);
            await this.RefreshAsync();
        }

        /// <summary>卸载(二次确认:第一次点击只把按钮切成「确认卸载」)。</summary>
        private async void OnUninstallClick(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.DataContext is not PluginEntry entry)
            {
                return;
            }

            if (!string.Equals(this._pendingUninstall, entry.Name, StringComparison.Ordinal))
            {
                this._pendingUninstall = entry.Name;
                button.Content = "确认卸载";
                _ = this.RestoreUninstallButtonAsync(entry.Name, button);
                return;
            }

            this._pendingUninstall = null;
            button.Content = "卸载";
            await this._plugins.UninstallAsync(entry.Name);
            await this.RefreshAsync();
        }

        /// <summary>5 秒内没有再点,自动撤回「确认卸载」状态,避免误触。</summary>
        private async Task RestoreUninstallButtonAsync(string packageName, Button button)
        {
            await Task.Delay(5000);

            if (string.Equals(this._pendingUninstall, packageName, StringComparison.Ordinal)
                && button.Content as string == "确认卸载")
            {
                this._pendingUninstall = null;
                button.Content = "卸载";
            }
        }

        private void OnEntryFilterChanged(object? sender, TextChangedEventArgs e) => this.ApplyEntryFilter();

        private void OnRefreshPluginsClick(object? sender, RoutedEventArgs e) => _ = this.RefreshAsync();

        private void OnRevealProfileClick(object? sender, RoutedEventArgs e) => this._plugins.OpenProfileDirectory();

        /// <summary>「安装新插件」视图的下标(0=已安装,1=安装新插件)。</summary>
        private const int InstallViewIndex = 1;

        /// <summary>
        /// 视图切换:右栏两个面板二选一,左栏只显示当前视图用得上的选项。
        /// 隐藏的面板 IsVisible=false,其中的控件不在 UIA 树里。
        /// </summary>
        private void OnPluginsViewChanged(object? sender, SelectionChangedEventArgs e)
        {
            this.ApplyView();

            // 首次进「安装新插件」时才拉目录(3-7MB,不必在打开插件页时就下载);
            // 之后切回来命中的是内存里的那份,不会重复下载
            if (this.PluginsViewList.SelectedIndex == InstallViewIndex && this._catalog is null)
            {
                _ = this.EnsureCatalogAsync();
            }
        }

        /// <summary>按当前视图切换右栏面板与左栏选项的可见性。</summary>
        private void ApplyView()
        {
            var install = this.PluginsViewList.SelectedIndex == InstallViewIndex;
            this.InstallView.IsVisible = install;
            this.InstalledView.IsVisible = !install;
            this.CatalogOptionsPanel.IsVisible = install;
            this.ManualInstallPanel.IsVisible = install;
            this.EntriesFilterPanel.IsVisible = !install;
        }

        /// <summary>
        /// 拉取策展目录并刷新列表。
        /// <list type="bullet">
        /// <item>已有该来源的缓存(在服务层,键是地址)→ 立即显示,切换来源是瞬时的。</item>
        /// <item>同一地址已在拉取 → 不重复发起(但用户又切了来源会另发起,见下)。</item>
        /// <item>每次发起都取新世代号,回调时过期则丢弃 —— 避免慢的旧来源覆盖快的新来源。</item>
        /// </list>
        /// </summary>
        private async Task EnsureCatalogAsync(bool forceReload = false)
        {
            var url = PluginService.ResolveCatalogUrl();

            // ① 命中缓存:立即显示。
            //    这里也要自增世代号 —— 否则“A→B→A”时,A 命中缓存秒显,
            //    随后在飞的 B 请求完成(号还等于当前号)会把列表改成 B。
            if (!forceReload && PluginService.Instance.GetCachedCatalog(url) is { } cached)
            {
                this._catalogRequestId++;
                this._catalogLoadingUrl = null;
                this._catalog = cached;
                this.ApplyCatalog(cached);
                this.UpdateBusy();
                return;
            }

            // ② 同一地址已在拉取且不是强制刷新 → 不重复发起
            if (!forceReload && string.Equals(this._catalogLoadingUrl, url, StringComparison.Ordinal))
            {
                return;
            }

            var requestId = ++this._catalogRequestId;
            this._catalogLoadingUrl = url;
            this.UpdateBusy();
            this.CatalogStatusText.Text = "正在读取插件目录…";
            try
            {
                var catalog = await PluginService.Instance.LoadCatalogAsync(forceReload);

                // 期间用户又切了来源 / 又点过刷新 → 丢弃本次结果(服务层已缓存,切回来即命中)
                if (requestId != this._catalogRequestId)
                {
                    return;
                }

                this._catalog = catalog;
                this.ApplyCatalog(catalog);
            }
            catch (Exception ex)
            {
                if (requestId != this._catalogRequestId)
                {
                    return;
                }

                // 上游刻意不拿旧数据冒充答案:这里也不回退缓存,直接把原因显示出来
                this.CatalogStatusText.Text =
                    $"读取插件目录失败:{ex.Message}{Environment.NewLine}"
                    + $"地址:{url}{Environment.NewLine}"
                    + "可检查网络后点「刷新目录」重试。";
            }
            finally
            {
                // 只有还是最新那次请求才收尾;被顶替的请求不动状态(状态已归新请求所有)
                if (requestId == this._catalogRequestId)
                {
                    this._catalogLoadingUrl = null;
                    this.UpdateBusy();
                }
            }
        }

        /// <summary>把目录内容填进界面:状态行 + 分类下拉 + 结果列表。</summary>
        private void ApplyCatalog(PluginCatalog catalog)
        {
            // 来源地址与时间放 tooltip:状态行要留给计数,不然最小窗口下列表只剩一两行
            ToolTip.SetTip(this.CatalogStatusText,
                $"来源:{catalog.SourceUrl}{Environment.NewLine}"
                + $"目录更新于 {catalog.Updated},本地拉取于 {catalog.FetchedAtLocal:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}"
                + "只在本次运行内保留(上游刻意不做陈旧缓存),点「刷新目录」重新拉取。");

            // 分类下拉:第一项是“全部分类”(Tag 为空),其余用分类 id 当 Tag 供过滤用
            this._initializing = true;
            try
            {
                var items = new List<ComboBoxItem> { new() { Content = "全部分类", Tag = string.Empty } };
                items.AddRange(catalog.Categories
                    .OrderBy(pair => pair.Value, StringComparer.CurrentCulture)
                    .Select(pair => new ComboBoxItem { Content = pair.Value, Tag = pair.Key }));
                this.CatalogCategoryCombo.ItemsSource = items;
                this.CatalogCategoryCombo.SelectedIndex = 0;

                if (this.CatalogSortCombo.SelectedIndex < 0)
                {
                    this.CatalogSortCombo.SelectedIndex = (int)PluginCatalogSort.Stars;
                }
            }
            finally
            {
                this._initializing = false;
            }

            this.ApplyCatalogFilter();
        }

        /// <summary>按关键词 / 分类 / 排序刷新目录列表(纯内存过滤,3722 条也很快)。</summary>
        private void ApplyCatalogFilter()
        {
            if (this._catalog is null)
            {
                return;
            }

            var keyword = this.CatalogSearchBox.Text ?? string.Empty;
            var category = (this.CatalogCategoryCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty;
            var sort = this.CatalogSortCombo.SelectedIndex switch
            {
                (int)PluginCatalogSort.Downloads => PluginCatalogSort.Downloads,
                (int)PluginCatalogSort.Newest => PluginCatalogSort.Newest,
                _ => PluginCatalogSort.Stars,
            };

            var results = PluginService.FilterCatalog(this._catalog, keyword, category, sort);
            this.CatalogList.ItemsSource = results;

            // dsh-plugin.org 目录没有 updated 字段,此时不显示“目录更新于”
            var updated = this._catalog.Updated.Length > 0 ? $" · 目录更新于 {this._catalog.Updated}" : string.Empty;
            this.CatalogStatusText.Text = results.Count == this._catalog.Entries.Count
                ? $"共 {results.Count} 个插件{updated}"
                : $"命中 {results.Count} 个 / 全部 {this._catalog.Entries.Count} 个{updated}";
        }

        private void OnCatalogSearchTextChanged(object? sender, TextChangedEventArgs e)
        {
            if (!this._initializing)
            {
                this.ApplyCatalogFilter();
            }
        }

        private void OnCatalogFilterChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (!this._initializing)
            {
                this.ApplyCatalogFilter();
            }
        }

        private void OnRefreshCatalogClick(object? sender, RoutedEventArgs e) => _ = this.EnsureCatalogAsync(true);

        /// <summary>
        /// 目录来源下拉框变更:保存设置并加载新来源。
        /// **不传 forceReload** —— 已拉过的来源直接命中缓存、瞬时显示;
        /// 想强制重拉请用「刷新目录」按钮。
        /// </summary>
        private void OnCatalogSourceChanged(object? sender, SelectionChangedEventArgs e)
        {
            // _pageReady:构造期的自动选中不算用户操作(见 _pageReady 注释)
            if (!this._pageReady || this._initializing || this.CatalogSourceCombo.SelectedIndex < 0)
            {
                return;
            }

            var source = (PluginCatalogSource)this.CatalogSourceCombo.SelectedIndex;
            if (SettingsService.Instance.Settings.PluginCatalog == source)
            {
                return;
            }

            SettingsService.Instance.Update(s => s.PluginCatalog = source);
            _ = this.EnsureCatalogAsync();
        }

        /// <summary>目录条目行上的「安装」:按目录给的规格装,失败自动回退到预构建 tarball。</summary>
        private async void OnInstallCatalogEntryClick(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button { DataContext: PluginCatalogEntry entry })
            {
                return;
            }

            await this._plugins.InstallCatalogEntryAsync(entry);
            await this.RefreshAsync();
        }

        /// <summary>目录条目行上的「详情」:在浏览器里打开目录页面。</summary>
        private void OnOpenCatalogPageClick(object? sender, RoutedEventArgs e)
        {
            if (sender is Button { DataContext: PluginCatalogEntry entry } && entry.PageUrl.Length > 0)
            {
                WebOpener.OpenInBrowser(entry.PageUrl);
            }
        }

        private async void OnInstallPluginClick(object? sender, RoutedEventArgs e)
            => await this.InstallAsync(this.InstallSpecBox.Text);

        private async void OnInstallSpecKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                await this.InstallAsync(this.InstallSpecBox.Text);
            }
        }

        private async Task InstallAsync(string? spec)
        {
            spec = spec?.Trim();
            if (string.IsNullOrEmpty(spec))
            {
                this._plugins.AppendLog("请先填写要安装的插件包(包名、tarball、本地绝对路径或 git 地址)。");
                return;
            }

            await this._plugins.InstallAsync(spec);
            await this.RefreshAsync();
            this.InstallSpecBox.Text = string.Empty;
        }

        private void OnPluginLogAppended(string text) => Dispatcher.UIThread.Post(() =>
        {
            this.PluginLogTextBlock.Text = (this.PluginLogTextBlock.Text ?? string.Empty) + text;
            this.PluginLogScroll.ScrollToEnd();

            // 浮窗关着时不给用户看日志,就在浮动按钮上亮一个圆点提示有新输出
            if (!this.PluginLogFlyout.IsVisible)
            {
                this.PluginLogUnreadDot.IsVisible = true;
            }
        });

        private void OnPluginLogsCleared() => Dispatcher.UIThread.Post(() => this.PluginLogTextBlock.Text = string.Empty);

        private void OnPluginStateChanged() => Dispatcher.UIThread.Post(() =>
        {
            this.UpdateBusy();

            // 安装/卸载一开始就自动弹出输出浮窗:这类操作要等网络,用户需要立刻看到进度与报错
            if (this._plugins.IsBusy && !this.PluginLogFlyout.IsVisible)
            {
                this.SetPluginLogVisible(true);
            }
        });

        /// <summary>浮动按钮:开/关输出浮窗。</summary>
        private void OnPluginLogToggleClick(object? sender, RoutedEventArgs e)
            => this.SetPluginLogVisible(!this.PluginLogFlyout.IsVisible);

        /// <summary>点了浮窗之外的空白处:关闭浮窗(点击不再往下传递,与 light dismiss 一致)。</summary>
        private void OnPluginLogDismissPressed(object? sender, PointerPressedEventArgs e)
        {
            this.SetPluginLogVisible(false);
            e.Handled = true;
        }

        private void OnClosePluginLogClick(object? sender, RoutedEventArgs e) => this.SetPluginLogVisible(false);

        /// <summary>显示/隐藏输出浮窗。打开时清掉未读小圆点并把日志滚到末尾。</summary>
        private void SetPluginLogVisible(bool visible)
        {
            this.PluginLogFlyout.IsVisible = visible;

            // 命中层与浮窗同进同出:只有浮窗开着时才拦截“点空白处”
            this.PluginLogDismissLayer.IsVisible = visible;

            if (!visible)
            {
                this.PluginLogUnreadDot.IsVisible = false;
                return;
            }

            this.PluginLogUnreadDot.IsVisible = false;
            this.PluginLogScroll.ScrollToEnd();
        }

        private async void OnCopyPluginLogClick(object? sender, RoutedEventArgs e)
        {
            if (await this.TryCopyTextAsync(this._plugins.LogText))
            {
                await this.FlashCopiedAsync();
            }
        }

        private void OnClearPluginLogClick(object? sender, RoutedEventArgs e) => this._plugins.ClearLog();

        /// <summary>把文本写入剪贴板;成功返回 true。剪贴板不可用时静默失败。</summary>
        private async Task<bool> TryCopyTextAsync(string text)
        {
            try
            {
                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clipboard is null)
                {
                    return false;
                }

                var transfer = new DataTransfer();
                transfer.Add(DataTransferItem.CreateText(text));
                await clipboard.SetDataAsync(transfer);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>复制反馈:按钮文字临时变成「已复制」,1.5 秒后还原(防连点)。</summary>
        private async Task FlashCopiedAsync()
        {
            if (this._copyFeedbackBusy)
            {
                return;
            }

            this._copyFeedbackBusy = true;
            try
            {
                this.CopyPluginLogButtonText.Text = "已复制";
                await Task.Delay(1500);
            }
            finally
            {
                this.CopyPluginLogButtonText.Text = "复制";
                this._copyFeedbackBusy = false;
            }
        }
    }
}
