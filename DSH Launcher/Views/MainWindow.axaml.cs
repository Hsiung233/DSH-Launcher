using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using DSH_Launcher.Services;
using FluentAvalonia.UI.Controls;

namespace DSH_Launcher.Views
{
    /// <summary>
    /// 主窗口:顶部导航(首页/设置),内容区切换(对应 WinUI 版 NavigationView + Frame)。
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly HomePageControl _homePage;
        private readonly PluginsPageControl _pluginsPage;
        private readonly SettingsPageControl _settingsPage;
        private readonly WindowStateTracker _windowStateTracker;

        public MainWindow()
        {
            InitializeComponent();

            // 窗口背景材质:Windows 11 用 Mica;其他平台用 Blur(macOS 毛玻璃/Linux blur-behind)。
            // 各平台不支持该级别时会自动回退,不影响功能。
            TransparencyLevelHint = new List<WindowTransparencyLevel>
            {
                OperatingSystem.IsWindows() ? WindowTransparencyLevel.Mica : WindowTransparencyLevel.Blur,
            };

            // 设置窗口/任务栏图标(logo-512.png 已嵌入程序集资源,见 AppIcon.LoadLogo512)
            this.Icon = AppIcon.LoadLogo512();

            // 恢复上次的位置/大小与最大化状态;没有有效记录时保持 XAML 初始尺寸 1280×720
            var restoredBounds = WindowStateService.Instance.RestoreMainWindow(this);
            this._windowStateTracker = WindowStateService.Instance.TrackMainWindow(this, restoredBounds);

            this._homePage = new HomePageControl();
            this._pluginsPage = new PluginsPageControl();
            this._settingsPage = new SettingsPageControl();

            this.NavView.Content = this._homePage;
            if (this.NavView.MenuItems.Count > 0)
            {
                this.NavView.SelectedItem = this.NavView.MenuItems[0];
            }
        }

        /// <summary>保存主窗口位置/大小与最大化状态(隐藏到托盘、退出应用前由 App 调用)。</summary>
        public void SaveWindowState() => this._windowStateTracker.Save();

        private void NavView_SelectionChanged(object? sender, FANavigationViewSelectionChangedEventArgs args)
        {
            if (args.SelectedItem is FANavigationViewItem item)
            {
                switch (item.Tag?.ToString())
                {
                    case "plugins":
                        this.NavView.Content = this._pluginsPage;
                        break;
                    case "settings":
                        this.NavView.Content = this._settingsPage;
                        break;
                    default:
                        this.NavView.Content = this._homePage;
                        break;
                }
            }
        }
    }
}
