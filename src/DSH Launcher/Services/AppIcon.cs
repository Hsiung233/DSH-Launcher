using System;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace DSH_Launcher.Services
{
    /// <summary>
    /// 应用图标加载。logo 以 Avalonia 资源(avares://)打进程序集,发布目录不再需要 Assets 文件夹;
    /// exe 自身的文件图标仍由 csproj 的 ApplicationIcon(编译期嵌入 .ico)提供,与此无关。
    /// </summary>
    public static class AppIcon
    {
        private static WindowIcon? _logo512;

        /// <summary>从嵌入资源加载 512px logo,用于窗口标题栏/托盘图标;失败返回 null(不影响功能)。</summary>
        public static WindowIcon? LoadLogo512()
        {
            // 一次加载后复用:Bitmap 不释放,进程级单份
            if (_logo512 is not null)
            {
                return _logo512;
            }

            try
            {
                // 资源 URI 的程序集名 = 生成程序集的简单名("DSH Launcher",带空格);
                // 显式传本程序集,不依赖"入口程序集"的隐式解析,从任意调用方都稳定。
                var uri = new Uri("avares://DSH Launcher/Assets/logo-512.png");
                using var stream = AssetLoader.Open(uri, new Uri("avares://DSH Launcher"));
                _logo512 = new WindowIcon(new Bitmap(stream));
                return _logo512;
            }
            catch (Exception)
            {
                // 资源缺失/损坏不影响主功能,只丢图标
                return null;
            }
        }
    }
}
