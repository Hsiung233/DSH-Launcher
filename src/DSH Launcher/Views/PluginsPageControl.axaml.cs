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
using DSH_Launcher.Models;
using DSH_Launcher.Services;
using DSH_Launcher.Views.Shared;

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

        /// <summary>
        /// 目录加载状态机:缓存命中、同地址去重、世代号(防"慢的旧来源覆盖快的新来源")。
        /// 这段逻辑与界面无关,单独成类并被单元测试覆盖 —— 见 <see cref="CatalogLoadController"/>。
        /// </summary>
        private readonly CatalogLoadController _catalogLoader;

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

        /// <summary>单列表当前实际显示的条目(受「显示全部」与筛选框影响),批量选择以它为准。</summary>
        private IReadOnlyList<PluginEntry> _entriesView = [];

        /// <summary>已订阅选中态变化的条目(每次刷新重建,用于退订)。</summary>
        private readonly List<PluginEntry> _observedEntries = [];

        /// <summary>
        /// 卸载二次确认的世代号:每次置位自增,倒计时到点后只有"仍是本人置位的那一次"才撤回。
        /// </summary>
        private int _uninstallArmId;

        /// <summary>输出浮窗正文的刷新节流器(理由见 <see cref="LogAppendThrottle"/>)。</summary>
        private readonly LogAppendThrottle _pluginLogThrottle;

        /// <summary>「复制」按钮的反馈。</summary>
        private readonly CopyFeedback _logCopy;

        public PluginsPageControl()
        {
            InitializeComponent();
            AttachedToVisualTree += this.OnPageAttached;

            this._pluginLogThrottle = new LogAppendThrottle(this.RefreshPluginLog);
            this._logCopy = new CopyFeedback(this.CopyPluginLogButtonText, "复制");

            // 三个依赖都来自服务层:地址由设置决定、缓存与服务层共用、真拉取也走服务层
            this._catalogLoader = new CatalogLoadController(
                PluginService.ResolveCatalogUrl,
                url => PluginService.Instance.GetCachedCatalog(url),
                forceReload => PluginService.Instance.LoadCatalogAsync(forceReload));

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
        /// <param name="forcePnpmProbe">
        /// 是否重新探测 pnpm。界面上的「刷新」按钮传 true(提示里让用户"修好 pnpm 后点刷新",
        /// 不重探就看不到状态变化);其余刷新(进页面、操作后回填)用缓存值。
        /// </param>
        private async Task RefreshAsync(bool forcePnpmProbe = false)
        {
            if (this._loading)
            {
                return;
            }

            this._loading = true;
            this.UpdateBusy();
            try
            {
                var snapshot = await PluginService.Instance.LoadAsync(forcePnpmProbe);
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

            // ① 空态:一个用户插件都没有时提示去安装(列表本身照常显示有覆盖的条目)
            this.InstalledEmptyPanel.IsVisible = snapshot.Installed.Count == 0;

            // ② 单列表「插件与条目」(按「显示全部」与筛选框决定实际显示哪些,计数也在那里算)
            this.ApplyEntryFilter();

            // ③ pnpm 状态:安装/卸载都由 dsh plugin 转发给它,缺失时只提示、不禁用页面(启停不依赖 pnpm)
            this.PnpmBadge.IsVisible = !snapshot.PnpmAvailable;
            if (!snapshot.PnpmAvailable)
            {
                ToolTip.SetTip(this.PnpmBadge,
                    "未检测到可用的 pnpm:安装/卸载插件会经 dsh plugin 转发给 pnpm。"
                    + "可执行 npm install -g pnpm@10 修复后点「刷新」。");
            }

            // 计数按**包**算已安装数(条目是 entry 粒度,一个包会派生多条),按**条**算列表
            var packageCount = snapshot.Installed.Select(entry => entry.Name).Distinct(StringComparer.Ordinal).Count();
            var summary = $"profile “{PluginService.ProfileName}” · 已安装 {packageCount} 个插件"
                + $" · 条目 {snapshot.AllEntries.Count} 条 · {PluginService.ProfileDirectory}";
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

        /// <summary>
        /// 决定单列表「插件与条目」实际显示哪些行(包名或条目 id 的子串匹配)。
        /// 默认只显示"与用户有关"的条目(已安装插件的条目 + 有启停覆盖的);
        /// 完整组合树(含 dsh 自带条目)由「显示全部」开关放出,搜索时始终搜全部
        /// (用户输关键词就是想找具体条目,不该被默认收起挡住)。
        /// </summary>
        private void ApplyEntryFilter()
        {
            var all = this._snapshot?.AllEntries ?? (IReadOnlyList<PluginEntry>)[];
            var filter = this.EntryFilterBox.Text?.Trim() ?? string.Empty;
            var showAll = filter.Length > 0 || this.ShowAllEntriesBox.IsChecked == true;

            IEnumerable<PluginEntry> query = all;
            if (!showAll)
            {
                query = query.Where(entry => entry.IsInstalled || entry.HasOverride);
            }

            if (filter.Length > 0)
            {
                query = query.Where(entry =>
                        entry.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                        || entry.Id.Contains(filter, StringComparison.OrdinalIgnoreCase));
            }

            var view = query as IReadOnlyList<PluginEntry> ?? query.ToList();

            // 记下当前显示的这批:批量选择/全选都以“看得见的”为准
            this._entriesView = view;
            this.AllEntriesList.ItemsSource = view;

            var packages = (this._snapshot?.Installed ?? (IReadOnlyList<PluginEntry>)[])
                .Select(entry => entry.Name)
                .Distinct(StringComparer.Ordinal)
                .Count();
            var tail = view.Count == all.Count
                ? $"共 {all.Count} 条"
                : $"显示 {view.Count} 条 / 共 {all.Count} 条";
            this.AllEntriesCountText.Text = $"已安装 {packages} 个插件 · {tail}";
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

            // 单列表数据源就是 AllEntries(Installed 是它的子集、同一批实例),订阅它即可
            this._observedEntries.AddRange(snapshot.AllEntries);

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

        /// <summary>当前列表里可见的条目(受「显示全部」与筛选框影响),批量选择/全选都以它为准。</summary>
        private IReadOnlyList<PluginEntry> VisibleEntries() => this._entriesView;

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
                ? "所选条目里没有可启停的(只有已安装到 profile 的插件可启停)"
                : $"对所选中的 {toggleable} 个条目写入 disabled: false");
            ToolTip.SetTip(this.BatchDisableButton, toggleable == 0
                ? "所选条目里没有可启停的(只有已安装到 profile 的插件可启停)"
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

            var succeeded = await this._plugins.UninstallBatchAsync(names);
            await this.RefreshAsync();
            if (succeeded > 0)
            {
                await this.PromptRestartAfterUninstallAsync(succeeded);
            }
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

        /// <summary>
        /// 安装成功后,若 dsh 服务正在运行则询问是否立即重启让它生效。
        /// 依据(已核实 dsh 源码):新装插件加入 profile 的 bundles 层,而层组合只在
        /// <c>dsh web</c> 启动时装配 —— 安装只改 package.json 与 node_modules,
        /// 运行中的进程不感知,刷新 Web 页面也无法生效;启用/禁用(写
        /// cordis.patch.yml)才走 live 热重载。dsh 未运行时不需要提示,
        /// 下次启动自然带上新插件。
        /// </summary>
        private Task PromptRestartAfterInstallAsync(string pluginName)
            => this.PromptRestartForChangeAsync(
                $"已安装 {pluginName}。dsh 服务正在运行,新插件要在服务下次启动时才会加载"
                + "(bundle 层只在启动时装配,刷新 Web 页面无法生效)。");

        /// <summary>卸载成功后,若 dsh 服务正在运行则询问是否立即重启以移除该插件。</summary>
        private Task PromptRestartAfterUninstallAsync(int count)
            => this.PromptRestartForChangeAsync(count == 1
                ? "已卸载插件。运行中的 dsh 服务要重启后才会移除它。"
                : $"已卸载 {count} 个插件。运行中的 dsh 服务要重启后才会移除它们。");

        /// <summary>变更后询问是否立即重启 dsh(安装/卸载共用;dsh 未运行时直接跳过)。</summary>
        private async Task PromptRestartForChangeAsync(string changeText)
        {
            if (!DshService.Instance.IsRunning)
            {
                return;
            }

            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is not Window owner)
            {
                return;
            }

            var dialog = new FluentAvalonia.UI.Controls.FAContentDialog
            {
                Title = "重启 dsh 服务?",
                Content = new TextBlock
                {
                    Text = $"{changeText}\r\n\r\n要现在重启 dsh 服务让变更生效吗?(重启会结束当前会话)",
                    TextWrapping = TextWrapping.Wrap,
                },
                PrimaryButtonText = "立即重启",
                CloseButtonText = "稍后再说",
                DefaultButton = FluentAvalonia.UI.Controls.FAContentDialogButton.Primary,
            };

            var result = await dialog.ShowAsync(owner);
            if (result != FluentAvalonia.UI.Controls.FAContentDialogResult.Primary)
            {
                this._plugins.AppendSystemLog("变更将在下次启动 dsh 服务时生效。");
                return;
            }

            var ok = await DshService.Instance.RestartAsync();
            if (ok)
            {
                this._plugins.AppendSystemLog("已重启 dsh 服务,变更已生效。");
                await this.RefreshAsync();
            }
            else
            {
                // 失败详情 DshService 已写进 app.log 与首页面板,这里只指路
                this._plugins.AppendSystemLog("重启 dsh 服务失败,请到首页查看启动日志。");
            }
        }

        private void UpdateBusy()
        {
            var busy = this._loading || this._catalogLoader.IsLoading || this._plugins.IsBusy;
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
                + "," + DescribeToggleEffect());
            await this.RefreshAsync();
        }

        /// <summary>
        /// 按运行基线描述启停覆盖的生效方式。判断依据是
        /// <see cref="DshService.RuntimePatchReload"/>(启动当刻的磁盘配置 = 运行实例的真实配置):
        /// live ⇒ 写完 cordis.patch.yml 即热生效,不必重启;startup ⇒ 必须重启;
        /// dsh 未运行 ⇒ 下次启动自然按新文件加载。
        /// </summary>
        private static string DescribeToggleEffect()
        {
            var dsh = DshService.Instance;
            if (!dsh.IsRunning)
            {
                return "下次启动 dsh 服务时生效";
            }

            // 基线为空 = 本次运行不是启动器拉起的(用户手动跑的),按 dsh 缺省 live 提示,
            // 并用"可刷新页面"给用户一个可自行验证的动作
            return dsh.RuntimePatchReload == "startup"
                ? "需重启 dsh 服务后生效"
                : "已热生效,如界面未变可刷新 Web 页面";
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
            if (sender is not Button { DataContext: PluginEntry entry })
            {
                return;
            }

            if (!entry.IsPendingUninstall)
            {
                this.ArmUninstallConfirmation(entry);
                return;
            }

            entry.IsPendingUninstall = false;
            if (await this._plugins.UninstallAsync(entry.Name))
            {
                await this.RefreshAsync();
                await this.PromptRestartAfterUninstallAsync(1);
            }
            else
            {
                await this.RefreshAsync();
            }
        }

        /// <summary>
        /// 把某一条置为「待确认卸载」,并在 5 秒后自动撤回(避免误触)。
        /// 状态写在数据对象上而不是按钮上 —— 理由见 <see cref="PluginEntry.IsPendingUninstall"/>。
        /// </summary>
        private void ArmUninstallConfirmation(PluginEntry entry)
        {
            // 同一时刻只允许一条待确认:否则用户点了 A 又点 B,两行都显示「确认卸载」
            foreach (var other in this.VisibleEntries())
            {
                if (!ReferenceEquals(other, entry))
                {
                    other.IsPendingUninstall = false;
                }
            }

            entry.IsPendingUninstall = true;

            // 世代号:期间若同一条又被重新置位,旧的倒计时不该撤销新的状态
            var armId = ++this._uninstallArmId;
            _ = this.DisarmUninstallConfirmationAsync(entry, armId);
        }

        /// <summary>5 秒内没有再点,自动撤回「确认卸载」状态。</summary>
        private async Task DisarmUninstallConfirmationAsync(PluginEntry entry, int armId)
        {
            await Task.Delay(5000);

            if (armId == this._uninstallArmId)
            {
                entry.IsPendingUninstall = false;
            }
        }

        /// <summary>筛选框内容变化:重滤「全部组合条目」。</summary>
        private void OnEntryFilterChanged(object? sender, TextChangedEventArgs e) => this.ApplyEntryFilter();

        /// <summary>「显示全部」开关:放出/收起 dsh 自带条目(纯内存重滤)。</summary>
        private void OnShowAllEntriesClick(object? sender, RoutedEventArgs e) => this.ApplyEntryFilter();

        private void OnRefreshPluginsClick(object? sender, RoutedEventArgs e) => _ = this.RefreshAsync(forcePnpmProbe: true);

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
            if (this.PluginsViewList.SelectedIndex == InstallViewIndex && this._catalogLoader.Catalog is null)
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
        /// 缓存命中 / 同地址去重 / 世代号(防慢的旧来源覆盖新来源)都在
        /// <see cref="CatalogLoadController"/> 里,这里只按结果更新界面。
        /// </summary>
        private async Task EnsureCatalogAsync(bool forceReload = false)
        {
            var result = await this._catalogLoader.EnsureAsync(forceReload, onStarted: () =>
            {
                // 真正发起拉取时才显示"正在读取"(命中缓存是瞬时的,不该闪一下)
                this.CatalogStatusText.Text = "正在读取插件目录…";
                this.UpdateBusy();
            });

            switch (result.Outcome)
            {
                case CatalogLoadOutcome.DisplayedFromCache:
                case CatalogLoadOutcome.Loaded:
                    this.ApplyCatalog(result.Catalog!);
                    this.UpdateBusy();
                    break;

                case CatalogLoadOutcome.Failed:
                    // 上游刻意不拿旧数据冒充答案:这里也不回退缓存,直接把原因显示出来
                    this.CatalogStatusText.Text =
                        $"读取插件目录失败:{result.Error}{Environment.NewLine}"
                        + $"地址:{this._catalogLoader.LastRequestedUrl}{Environment.NewLine}"
                        + "可检查网络后点「刷新目录」重试。";
                    this.UpdateBusy();
                    break;

                case CatalogLoadOutcome.Superseded:
                case CatalogLoadOutcome.AlreadyLoading:
                    // 被顶替的请求不动状态(状态已归新请求所有);同地址重复发起的请求无事可做
                    break;
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
            if (this._catalogLoader.Catalog is not { } catalog)
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

            var results = PluginService.FilterCatalog(catalog, keyword, category, sort);
            this.CatalogList.ItemsSource = results;

            // dsh-plugin.org 目录没有 updated 字段,此时不显示“目录更新于”
            var updated = catalog.Updated.Length > 0 ? $" · 目录更新于 {catalog.Updated}" : string.Empty;
            this.CatalogStatusText.Text = results.Count == catalog.Entries.Count
                ? $"共 {results.Count} 个插件{updated}"
                : $"命中 {results.Count} 个 / 全部 {catalog.Entries.Count} 个{updated}";
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

            var ok = await this._plugins.InstallCatalogEntryAsync(entry);
            await this.RefreshAsync();
            if (ok)
            {
                await this.PromptRestartAfterInstallAsync(entry.Name);
            }
        }

        /// <summary>目录条目行上的「详情」:在浏览器里打开目录页面。</summary>
        private void OnOpenCatalogPageClick(object? sender, RoutedEventArgs e)
        {
            if (sender is Button { DataContext: PluginCatalogEntry entry } && entry.PageUrl.Length > 0)
            {
                BrowserLauncher.OpenInBrowser(entry.PageUrl);
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

            var ok = await this._plugins.InstallAsync(spec);
            await this.RefreshAsync();
            this.InstallSpecBox.Text = string.Empty;
            if (ok)
            {
                await this.PromptRestartAfterInstallAsync(spec);
            }
        }

        private void OnPluginLogAppended(string text) => Dispatcher.UIThread.Post(() =>
        {
            // 浮窗关着时不给用户看日志,就在浮动按钮上亮一个圆点提示有新输出。
            // 这步很轻,不必等节流 —— 亮了圆点用户才知道有输出。
            if (!this.PluginLogFlyout.IsVisible)
            {
                this.PluginLogUnreadDot.IsVisible = true;
            }

            // 正文刷新走节流(见 LogAppendThrottle),内容从服务侧现取
            this._pluginLogThrottle.Schedule();
        });

        private void RefreshPluginLog()
        {
            this.PluginLogTextBlock.Text = this._plugins.LogText;

            // 浮窗关着时滚动没有意义(也没得看)
            if (this.PluginLogFlyout.IsVisible)
            {
                this.PluginLogScroll.ScrollToEnd();
            }
        }

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
            await this._logCopy.CopyAsync(this, this._plugins.LogText);
        }

        private void OnClearPluginLogClick(object? sender, RoutedEventArgs e) => this._plugins.ClearLog();

    }
}
