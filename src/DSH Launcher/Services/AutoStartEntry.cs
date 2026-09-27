using System;
using System.IO;

namespace DSH_Launcher.Services
{
    /// <summary>
    /// “开机自启动”在操作系统侧的落脚点:注册到哪、写成什么内容。
    /// <para>
    /// 为什么单独成文件:这里是**格式知识**(注册表值名、LaunchAgent 的 plist、XDG 的 .desktop 文本),
    /// 与“什么时候该写、写失败怎么办”是两件事。把它做成纯字符串拼装,单元测试就能直接盯住格式
    /// (与 <c>PluginPatchFile</c> / <c>PluginCatalogReader</c> 同一路数);而 <see cref="AutoStartService"/>
    /// 只管读写与失败处理。
    /// </para>
    /// <para>
    /// 三个平台的落点(都是**当前用户**级、都不需要管理员权限,与安装包 PrivilegesRequired=lowest 一致):
    /// <list type="bullet">
    /// <item>Windows:注册表 <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c> 下名为 <see cref="ValueName"/>
    /// 的字符串值。登录时由系统直接创建进程,不经过任何 shell ⇒ **没有黑框闪现**(这正是没用
    /// “启动文件夹里放一个 .cmd”的原因)。</item>
    /// <item>macOS:<c>~/Library/LaunchAgents/&lt;label&gt;.plist</c> + RunAtLoad。</item>
    /// <item>Linux:<c>~/.config/autostart/&lt;name&gt;.desktop</c>(XDG autostart 规范,GNOME/KDE 都认)。</item>
    /// </list>
    /// </para>
    /// </summary>
    internal static class AutoStartEntry
    {
        /// <summary>Windows Run 项下的值名(也是提示文案里给用户看的名字)。</summary>
        public const string ValueName = "DSH Launcher";

        /// <summary>Windows 的 Run 键(相对 HKEY_CURRENT_USER)。</summary>
        public const string WindowsRunSubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        /// <summary>macOS LaunchAgent 的 Label(Apple 惯例用反向域名,同时用作 plist 文件名)。</summary>
        public const string MacOSLaunchAgentLabel = "com.dsh-launcher.autostart";

        /// <summary>Linux XDG autostart 的文件名。</summary>
        public const string LinuxDesktopFileName = "dsh-launcher.desktop";

        /// <summary>注册项给程序带上的“本次是系统拉起”的标记(与 <see cref="StartupArguments"/> 同一常量)。</summary>
        private const string Switch = StartupArguments.AutoStartSwitch;

        /// <summary>Windows Run 项的值:带引号的完整路径 + 标记参数(路径含空格,不引号会被拆成两段)。</summary>
        public static string WindowsCommand(string executablePath) => $"\"{executablePath}\" {Switch}";

        /// <summary>
        /// 从 Run 项的值里取出可执行文件路径;取不到(空值)返回 null。
        /// <para>
        /// 要解析是因为自启动项可能是**旧版本/用户手工**写的:路径比较后才知道它是不是还指向当前程序
        /// (程序换了安装目录时,注册项会静默指着已经不存在的 exe)。
        /// </para>
        /// </summary>
        public static string? ParseWindowsCommand(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var text = value.Trim();
            if (text[0] == '"')
            {
                var end = text.IndexOf('"', 1);

                // 只有开引号、没有闭引号(手工写坏):整段当路径,总比丢掉强
                return end > 1 ? text[1..end] : text[1..];
            }

            // 未加引号(手工写的常见形态):取到第一个空格为止
            var space = text.IndexOf(' ');
            return space > 0 ? text[..space] : text;
        }

        /// <summary>
        /// 自启动项指向的是不是当前程序。
        /// <para>
        /// 路径比较**不区分大小写**:Windows / macOS 的文件系统本来就不敏感,Linux 上放宽容的后果也只是
        /// 少报一次“路径不同”(不影响任何实际行为),而写成平台分支反而让这个纯函数变得难测。
        /// </para>
        /// </summary>
        public static bool IsCurrentTarget(string? target, string executablePath)
            => target is not null && string.Equals(target, executablePath, StringComparison.OrdinalIgnoreCase);

        /// <summary>macOS / Linux 的自启动项文件路径;其他平台为 null(Windows 用注册表)。</summary>
        public static string? EntryFilePath
        {
            get
            {
                if (OperatingSystem.IsMacOS())
                {
                    return Path.Combine(HomeDirectory, "Library", "LaunchAgents", MacOSLaunchAgentLabel + ".plist");
                }

                if (OperatingSystem.IsLinux())
                {
                    return Path.Combine(HomeDirectory, ".config", "autostart", LinuxDesktopFileName);
                }

                return null;
            }
        }

        /// <summary>按当前平台给出自启动项文件内容(macOS 是 plist,其余文件平台是 .desktop)。</summary>
        public static string EntryFileContent(string executablePath) => OperatingSystem.IsMacOS()
            ? MacOSLaunchAgentPlist(executablePath)
            : LinuxDesktopEntry(executablePath);

        /// <summary>macOS LaunchAgent 的 plist 正文。</summary>
        public static string MacOSLaunchAgentPlist(string executablePath)
        {
            var lines = new[]
            {
                """<?xml version="1.0" encoding="UTF-8"?>""",
                """<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">""",
                """<plist version="1.0">""",
                "<dict>",
                "    <key>Label</key>",
                $"    <string>{MacOSLaunchAgentLabel}</string>",
                "    <key>ProgramArguments</key>",
                "    <array>",
                $"        <string>{EscapeXml(executablePath)}</string>",
                $"        <string>{Switch}</string>",
                "    </array>",
                "    <key>RunAtLoad</key>",
                "    <true/>",
                "</dict>",
                "</plist>",
                "",
            };

            return string.Join("\n", lines);
        }

        /// <summary>
        /// Linux XDG autostart 的 .desktop 正文。
        /// <c>X-GNOME-Autostart-enabled</c> 是 GNOME 认的开关(KDE 只看文件在不在),
        /// 既然“禁用 = 删文件”,这里恒为 true 即可。
        /// </summary>
        public static string LinuxDesktopEntry(string executablePath)
        {
            var lines = new[]
            {
                "[Desktop Entry]",
                "Type=Application",
                $"Name={ValueName}",
                "Comment=DSH 桌面启动器(常驻托盘的 dsh web 外壳)",
                $"Exec=\"{executablePath}\" {Switch}",
                "Terminal=false",
                "X-GNOME-Autostart-enabled=true",
                "",
            };

            return string.Join("\n", lines);
        }

        /// <summary>
        /// XML 正文转义。只处理正文里必须转义的三个字符(&amp; 必须最先替换,否则会把后面替换出来的
        /// &amp;lt; 二次转义);路径里出现引号不需要处理 —— 它落在元素内容里而不是属性值里。
        /// </summary>
        private static string EscapeXml(string value) => value
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;");

        /// <summary>用户主目录(取不到时退回 HOME 环境变量,与 <c>PlatformProcess</c> 的防御一致)。</summary>
        private static string HomeDirectory
        {
            get
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return string.IsNullOrEmpty(home)
                    ? Environment.GetEnvironmentVariable("HOME") ?? string.Empty
                    : home;
            }
        }
    }
}
