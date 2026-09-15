using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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

        /// <summary>正在拉取插件目录。</summary>
        private bool _catalogLoading;

        /// <summary>回填下拉框/文本框或重建分类列表时置位,避免把回填当成用户操作。</summary>
        private bool _initializing;

        /// <summary>正在读取插件清单(与服务的忙碌状态共同决定进度环与按钮可用性)。</summary>
        private bool _loading;

        /// <summary>卸载的二次确认状态(点击「卸载」→ 变成「确认卸载」,5 秒内再点才真的卸载)。</summary>
        private string? _pendingUninstall;

        private bool _copyFeedbackBusy;

        public PluginsPageControl()
        {
            InitializeComponent();
            AttachedToVisualTree += this.OnPageAttached;

            this._plugins.LogAppended += this.OnPluginLogAppended;
            this._plugins.LogsCleared += this.OnPluginLogsCleared;
            this._plugins.StateChanged += this.OnPluginStateChanged;
        }

        /// <summary>进入页面时回填设置并后台读取插件清单(不阻塞首屏)。
        /// 插件目录不在这里拉(约 3MB):等切到「安装新插件」页签时才拉。</summary>
        private void OnPageAttached(object? sender, EventArgs e)
        {
            this._initializing = true;
            try
            {
                var settings = SettingsService.Instance.Settings;
                this.CatalogSourceCombo.SelectedIndex = (int)settings.PluginCatalog;
                this.CatalogUrlBox.Text = settings.PluginCatalogUrl;
            }
            finally
            {
                this._initializing = false;
            }

            this.UpdateCatalogSourceUi();
            _ = this.RefreshAsync();

            // 首次进入时若已经停在安装页签(比如用户上次就在这),也要把目录拉起来
            if (this.PluginsTabs.SelectedIndex == InstallTabIndex)
            {
                _ = this.EnsureCatalogAsync();
            }
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

            this.AllEntriesList.ItemsSource = filter.Length == 0
                ? all
                : all.Where(entry =>
                        entry.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                        || entry.Id.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    .ToList();
        }

        private void UpdateBusy()
        {
            var busy = this._loading || this._catalogLoading || this._plugins.IsBusy;
            this.PluginsProgress.IsActive = busy;
            this.RefreshPluginsButton.IsEnabled = !busy;
            this.RevealProfileButton.IsEnabled = !busy;
            this.RefreshCatalogButton.IsEnabled = !busy;
            this.InstallPluginButton.IsEnabled = !busy;
        }

        /// <summary>空态里的「去安装新插件」:切到第 2 个页签(0=已安装,1=安装新插件)。</summary>
        private void OnGoToInstallTabClick(object? sender, RoutedEventArgs e)
            => this.PluginsTabs.SelectedIndex = 1;

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

        /// <summary>「安装新插件」页签的下标(0=已安装,1=安装新插件)。</summary>
        private const int InstallTabIndex = 1;

        /// <summary>
        /// 页签切换:首次进「安装新插件」时才拉目录(约 3MB,不必在打开插件页时就下载)。
        /// 之后切回来命中的是内存里的那份,不会重复下载。
        /// </summary>
        private void OnPluginsTabChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (this.PluginsTabs.SelectedIndex == InstallTabIndex && this._catalog is null)
            {
                _ = this.EnsureCatalogAsync();
            }
        }

        /// <summary>拉取策展目录并刷新列表。已有内存缓存时不会重新下载(forceReload=true 才会)。</summary>
        private async Task EnsureCatalogAsync(bool forceReload = false)
        {
            if (this._catalogLoading)
            {
                return;
            }

            this._catalogLoading = true;
            this.UpdateBusy();
            this.CatalogStatusText.Text = "正在读取插件目录…";
            try
            {
                var catalog = await PluginService.Instance.LoadCatalogAsync(forceReload);
                this._catalog = catalog;
                this.ApplyCatalog(catalog);
            }
            catch (Exception ex)
            {
                // 上游刻意不拿旧数据冒充答案:这里也不回退缓存,直接把原因显示出来
                this.CatalogStatusText.Text =
                    $"读取插件目录失败:{ex.Message}{Environment.NewLine}"
                    + $"地址:{PluginService.ResolveCatalogUrl()}{Environment.NewLine}"
                    + "可检查网络后点「刷新目录」;若本机访问不了官方站点,可把来源改成自建镜像。";
            }
            finally
            {
                this._catalogLoading = false;
                this.UpdateBusy();
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

        /// <summary>目录来源下拉框变更:保存设置、同步地址框,并重新拉取(地址变了,旧缓存作废)。</summary>
        private void OnCatalogSourceChanged(object? sender, SelectionChangedEventArgs e)
        {
            this.UpdateCatalogSourceUi();
            if (this._initializing || this.CatalogSourceCombo.SelectedIndex < 0)
            {
                return;
            }

            var source = (PluginCatalogSource)this.CatalogSourceCombo.SelectedIndex;
            SettingsService.Instance.Update(s => s.PluginCatalog = source);
            this.UpdateCatalogSourceUi();

            if (this.CatalogUrlBox.IsVisible && string.IsNullOrWhiteSpace(this.CatalogUrlBox.Text))
            {
                // 还没填镜像地址:不拉,给出提示
                this.CatalogStatusText.Text = "已选择自定义镜像,但地址为空。请填入返回同结构 plugins.json 的地址。";
                return;
            }

            _ = this.EnsureCatalogAsync(true);
        }

        private void OnCatalogUrlLostFocus(object? sender, RoutedEventArgs e)
        {
            if (this._initializing)
            {
                return;
            }

            var value = this.CatalogUrlBox.Text?.Trim() ?? string.Empty;
            if (SettingsService.Instance.Settings.PluginCatalogUrl == value)
            {
                return;
            }

            SettingsService.Instance.Update(s => s.PluginCatalogUrl = value);
            this.UpdateCatalogSourceUi();
            if (this.CatalogSourceCombo.SelectedIndex == (int)PluginCatalogSource.Custom && value.Length > 0)
            {
                _ = this.EnsureCatalogAsync(true);
            }
        }

        /// <summary>自定义镜像时才显示地址框(平时不占一行高度)。</summary>
        private void UpdateCatalogSourceUi()
            => this.CatalogUrlBox.IsVisible = this.CatalogSourceCombo.SelectedIndex == (int)PluginCatalogSource.Custom;

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

        private void OnClosePluginLogClick(object? sender, RoutedEventArgs e) => this.SetPluginLogVisible(false);

        /// <summary>显示/隐藏输出浮窗。打开时清掉未读小圆点并把日志滚到末尾。</summary>
        private void SetPluginLogVisible(bool visible)
        {
            this.PluginLogFlyout.IsVisible = visible;
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
