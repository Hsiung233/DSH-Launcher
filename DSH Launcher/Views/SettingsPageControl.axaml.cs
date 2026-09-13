using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using DSH_Launcher.Services;

namespace DSH_Launcher.Views
{
    public partial class SettingsPageControl : UserControl
    {
        // 初始化下拉框时会触发 SelectionChanged,用此标志避免无意义的保存
        private bool _initializing;

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
                // 设置页下拉框只有前三个动作(无动作/WebView/浏览器),枚举顺序一致,直接按索引映射
                this.AfterDshServiceStartedCombo.SelectedIndex = (int)settings.AfterDshServiceStarted;
                this.WebViewLinkCombo.SelectedIndex = (int)settings.WebViewLink;
                this.KeepWebViewAliveSwitch.IsChecked = settings.KeepWebViewAlive;

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
        }

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
    }
}
