using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using DSH_Launcher.Models;
using DSH_Launcher.Services;

namespace DSH_Launcher.Views
{
    public partial class SettingsPageControl : UserControl
    {
        // 初始化下拉框时会触发 SelectionChanged,用此标志避免无意义的保存
        private bool _initializing;

        /// <summary>
        /// 页面已就绪(首次进页面、设置已回填完)。
        /// ⚠ 必须有它:Avalonia 的 `ComboBox` 在**构造时就会自动选中第 0 项**,
        /// 那个 `SelectionChanged` 发生在 `InitializeComponent` 期间、早于 `AttachedToVisualTree`,
        /// 此时 `_initializing` 还没置位,会被当成用户操作写回设置
        /// (插件页就这样把用户选的来源静默改回过第 0 项)。
        /// </summary>
        private bool _pageReady;

        public SettingsPageControl()
        {
            InitializeComponent();
            AttachedToVisualTree += OnPageAttached;
        }

        private void OnPageAttached(object? sender, EventArgs e)
        {
            this._initializing = true;
            try
            {
                var settings = SettingsService.Instance.Settings;
                this.TraySingleClickCombo.SelectedIndex = (int)settings.TraySingleClick;
                this.TrayDoubleClickCombo.SelectedIndex = (int)settings.TrayDoubleClick;
                this.RunDshServiceOnStartupSwitch.IsChecked = settings.RunDshServiceOnStartup;
                this.ShowMainWindowOnStartupSwitch.IsChecked = settings.ShowMainWindowOnStartup;

                // 该下拉框的界面顺序与枚举值顺序不同,经 RepeatLaunchOptions 显式转换(见其注释)
                this.RepeatLaunchCombo.SelectedIndex = RepeatLaunchOptions.ToIndex(settings.RepeatLaunchAction);

                // 设置页下拉框只有前三个动作(无动作/WebView/浏览器),枚举顺序一致,直接按索引映射
                this.AfterDshServiceStartedCombo.SelectedIndex = (int)settings.AfterDshServiceStarted;
                this.WebViewLinkCombo.SelectedIndex = (int)settings.WebViewLink;
                this.KeepWebViewAliveSwitch.IsChecked = settings.KeepWebViewAlive;
                this.NpmRegistryCombo.SelectedIndex = (int)settings.NpmRegistry;
                this.ProxyUrlBox.Text = settings.ProxyUrl;
                this.NoProxyBox.Text = settings.NoProxy;
                this.UpdateRegistryHint();
                this.UpdateNoProxyEnabled();

                // 0 或超出范围视为“未设置”,留空(null)由控件显示占位提示
                var timeout = settings.WebViewIdleTimeoutMinutes;
                this.WebViewIdleTimeoutBox.Value =
                    timeout is >= AppSettings.MinWebViewIdleTimeoutMinutes and <= AppSettings.MaxWebViewIdleTimeoutMinutes
                        ? (decimal?)timeout
                        : null;

                this.UpdateWebViewIdleTimeoutEnabled();
            }
            finally
            {
                this._initializing = false;
            }

            // 回填完毕:之后的 SelectionChanged / LostFocus 才算用户操作
            this._pageReady = true;
        }

        /// <summary>
        /// 提示当前生效的 registry 地址。选“使用配置源”时说明会沿用本机配置
        /// (启动器读不到 .npmrc 的真实值 —— 那是 npm 自己解析的,所以不谎报一个地址)。
        /// </summary>
        private void UpdateRegistryHint()
        {
            var index = this.NpmRegistryCombo.SelectedIndex;
            var source = index >= 0 && Enum.IsDefined(typeof(NpmRegistrySource), index)
                ? (NpmRegistrySource)index
                : NpmRegistrySource.Config;
            var url = ChildEnvironment.ResolveRegistry(source);

            this.NpmRegistryUrlText.Text = url is null
                ? "当前:使用本机配置(.npmrc / 环境变量)"
                : $"当前:{ChildEnvironment.RegistryDisplayName(source)} {url}";
        }

        /// <summary>“不走代理”只在填了代理地址时才可编辑。</summary>
        private void UpdateNoProxyEnabled()
            => this.NoProxyPanel.IsEnabled = ChildEnvironment.NormalizeProxyUrl(this.ProxyUrlBox.Text).Length > 0;

        /// <summary>“保留超时”只在“保留 WebView 窗口”开启时才可编辑。</summary>
        private void UpdateWebViewIdleTimeoutEnabled()
        {
            this.WebViewIdleTimeoutPanel.IsEnabled = this.KeepWebViewAliveSwitch.IsChecked == true;
        }

        private void OnKeepWebViewAliveChanged(object? sender, RoutedEventArgs e)
        {
            // 联动要在 _initializing 判定之前:回填时也要正确反映可用状态
            this.UpdateWebViewIdleTimeoutEnabled();

            if (this._initializing)
            {
                return;
            }

            SettingsService.Instance.Update(
                s => s.KeepWebViewAlive = this.KeepWebViewAliveSwitch.IsChecked == true);
        }

        /// <summary>
        /// “保留超时”变更时写回设置。NumericUpDown 自带 Minimum/Maximum 约束,
        /// 留空(null)记作 0 = 使用默认 5 分钟。
        /// </summary>
        private void OnWebViewIdleTimeoutChanged(object? sender, NumericUpDownValueChangedEventArgs e)
        {
            if (this._initializing)
            {
                return;
            }

            var value = this.WebViewIdleTimeoutBox.Value;
            var minutes = 0;
            if (value is decimal v
                && v >= AppSettings.MinWebViewIdleTimeoutMinutes
                && v <= AppSettings.MaxWebViewIdleTimeoutMinutes)
            {
                minutes = (int)v;
            }

            if (SettingsService.Instance.Settings.WebViewIdleTimeoutMinutes == minutes)
            {
                return;
            }

            SettingsService.Instance.Update(s => s.WebViewIdleTimeoutMinutes = minutes);
        }

        private void OnWebViewLinkChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (this._initializing || this.WebViewLinkCombo.SelectedIndex < 0)
            {
                return;
            }

            SettingsService.Instance.Update(
                s => s.WebViewLink = (WebViewLinkTarget)this.WebViewLinkCombo.SelectedIndex);
        }

        private void OnTraySingleClickChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (this._initializing || this.TraySingleClickCombo.SelectedIndex < 0)
            {
                return;
            }

            SettingsService.Instance.Update(
                s => s.TraySingleClick = (WebOpenAction)this.TraySingleClickCombo.SelectedIndex);
        }

        private void OnTrayDoubleClickChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (this._initializing || this.TrayDoubleClickCombo.SelectedIndex < 0)
            {
                return;
            }

            SettingsService.Instance.Update(
                s => s.TrayDoubleClick = (WebOpenAction)this.TrayDoubleClickCombo.SelectedIndex);
        }

        private void OnRunDshServiceOnStartupChanged(object? sender, RoutedEventArgs e)
        {
            if (this._initializing)
            {
                return;
            }

            SettingsService.Instance.Update(
                s => s.RunDshServiceOnStartup = this.RunDshServiceOnStartupSwitch.IsChecked == true);
        }

        private void OnShowMainWindowOnStartupChanged(object? sender, RoutedEventArgs e)
        {
            if (this._initializing)
            {
                return;
            }

            SettingsService.Instance.Update(
                s => s.ShowMainWindowOnStartup = this.ShowMainWindowOnStartupSwitch.IsChecked == true);
        }

        private void OnAfterDshServiceStartedChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (this._initializing || this.AfterDshServiceStartedCombo.SelectedIndex < 0)
            {
                return;
            }

            SettingsService.Instance.Update(
                s => s.AfterDshServiceStarted = (WebOpenAction)this.AfterDshServiceStartedCombo.SelectedIndex);
        }

        /// <summary>
        /// “重复启动应用时”变更时写回设置。
        /// 构造期的自动选中(第 0 项)早于 <see cref="_pageReady"/>,不能当成用户操作,
        /// 否则会把用户的设置静默改回「无动作」。
        /// </summary>
        private void OnRepeatLaunchChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (!this._pageReady || this._initializing || this.RepeatLaunchCombo.SelectedIndex < 0)
            {
                return;
            }

            var action = RepeatLaunchOptions.FromIndex(this.RepeatLaunchCombo.SelectedIndex);
            if (SettingsService.Instance.Settings.RepeatLaunchAction == action)
            {
                return;
            }

            SettingsService.Instance.Update(s => s.RepeatLaunchAction = action);
        }

        /// <summary>npm 源变更:写设置并记录一行日志(便于从 app.log 核对实际生效的地址)。</summary>
        private void OnNpmRegistryChanged(object? sender, SelectionChangedEventArgs e)
        {
            // 构造期的自动选中(第 0 项)早于 _pageReady,不能当成用户操作
            if (!this._pageReady || this._initializing || this.NpmRegistryCombo.SelectedIndex < 0)
            {
                return;
            }

            this.UpdateRegistryHint();

            var source = (NpmRegistrySource)this.NpmRegistryCombo.SelectedIndex;
            if (SettingsService.Instance.Settings.NpmRegistry == source)
            {
                return;
            }

            SettingsService.Instance.Update(s => s.NpmRegistry = source);
            DshService.Instance.AppendSystemLog($"[环境] npm 源 = {ChildEnvironment.DescribeRegistry()}");
        }

        /// <summary>
        /// 代理框文字变化:更新“不走代理”的可用状态;若被清空则立即落盘(理由见
        /// <see cref="CommitClearedEnvironmentField"/>)。
        /// 其余情况仍只在失焦/回车时提交 —— 边打字边提交会把半截 URL 存进设置。
        /// </summary>
        private void OnProxyUrlTextChanged(object? sender, TextChangedEventArgs e)
        {
            this.UpdateNoProxyEnabled();

            if (string.IsNullOrEmpty(this.ProxyUrlBox.Text)
                && SettingsService.Instance.Settings.ProxyUrl.Length > 0)
            {
                this.CommitClearedEnvironmentField(s => s.ProxyUrl = string.Empty);
            }
        }

        /// <summary>“不走代理”框被清空(例如点了自带的「x」)时立即落盘;其余情况仍等失焦/回车。</summary>
        private void OnNoProxyTextChanged(object? sender, TextChangedEventArgs e)
        {
            if (string.IsNullOrEmpty(this.NoProxyBox.Text)
                && SettingsService.Instance.Settings.NoProxy.Length > 0)
            {
                this.CommitClearedEnvironmentField(s => s.NoProxy = string.Empty);
            }
        }

        /// <summary>
        /// 输入框被清空时立即把设置落盘。
        /// <para>
        /// 为什么要单独处理:本页的提交时机是「失焦 / 回车」,而 TextBox 自带的清除按钮是
        /// <c>Focusable=False</c> —— 点它**不会**让输入框失焦,于是"点了「x」后直接退出程序"
        /// 会把旧值留在设置里(下次启动又冒出来)。只给"清空"开这个口子:它是个完整的动作,
        /// 而边打字边提交会把半截 URL 存进设置。
        /// </para>
        /// </summary>
        private void CommitClearedEnvironmentField(Action<AppSettings> clear)
        {
            // 回填阶段不算用户操作(与 CommitEnvironmentInput 同一套防护)
            if (!this._pageReady || this._initializing)
            {
                return;
            }

            SettingsService.Instance.Update(clear);
            DshService.Instance.AppendSystemLog($"[环境] 代理 = {ChildEnvironment.DescribeProxy()}");
        }

        /// <summary>当前正在处理的输入框失焦/回车提交(见 <see cref="CommitEnvironmentInput"/>)。</summary>
        private void OnEnvironmentInputLostFocus(object? sender, FocusChangedEventArgs e) => this.CommitEnvironmentInput();

        private void OnEnvironmentInputKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                this.CommitEnvironmentInput();
            }
        }

        /// <summary>
        /// 提交代理相关的两个输入框:归一化后写设置,并把归一化结果回填回控件
        /// (用户填 127.0.0.1:7890 会被补全成 http://127.0.0.1:7890,当场可见)。
        /// </summary>
        private void CommitEnvironmentInput()
        {
            if (!this._pageReady || this._initializing)
            {
                return;
            }

            var proxy = ChildEnvironment.NormalizeProxyUrl(this.ProxyUrlBox.Text);
            var noProxy = ChildEnvironment.NormalizeNoProxy(this.NoProxyBox.Text);
            var settings = SettingsService.Instance.Settings;

            this.ProxyUrlBox.Text = proxy;
            this.NoProxyBox.Text = noProxy;
            this.UpdateNoProxyEnabled();

            if (settings.ProxyUrl == proxy && settings.NoProxy == noProxy)
            {
                return;
            }

            SettingsService.Instance.Update(s =>
            {
                s.ProxyUrl = proxy;
                s.NoProxy = noProxy;
            });
            DshService.Instance.AppendSystemLog($"[环境] 代理 = {ChildEnvironment.DescribeProxy()}");
        }
    }
}
