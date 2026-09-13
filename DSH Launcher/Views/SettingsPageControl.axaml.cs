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
            }
            finally
            {
                this._initializing = false;
            }
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
