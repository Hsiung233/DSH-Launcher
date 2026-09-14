using System;
using System.Diagnostics;
using System.IO;

namespace DSH_Launcher.Services
{
    /// <summary>
    /// 平台相关的"启动子进程"辅助(Windows / macOS / Linux)。
    /// 设计原则:Windows 分支与改造前的写法**逐字一致**(cmd.exe /c + call .cmd shim),
    /// 非 Windows 走等效分支,不改变 Windows 上的任何行为。
    /// </summary>
    public static class PlatformProcess
    {
        /// <summary>当前是否为 Windows(平台判断集中在此,便于审阅与后续扩展)。</summary>
        public static bool IsWindows { get; } = OperatingSystem.IsWindows();

        /// <summary>
        /// 用户级应用数据目录(Roaming 语义)。
        /// Windows:%APPDATA%;macOS:~/Library/Application Support(macOS 惯例;
        /// .NET 会把 ApplicationData 映射为 ~/.config,不符合 macOS 应用规范故显式覆盖);
        /// 其他平台:.NET 默认(Linux 为 ~/.config)。
        /// </summary>
        public static string RoamingAppDataDirectory => OperatingSystem.IsMacOS()
            ? GetMacOSApplicationSupportDirectory()
            : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        /// <summary>
        /// 用户级应用数据目录(Local 语义,不随漫游)。
        /// Windows:%LOCALAPPDATA%(取不到时回退 %LOCALAPPDATA% 环境变量,与既往防御一致);
        /// macOS:~/Library/Application Support;其他平台:.NET 默认。
        /// </summary>
        public static string LocalAppDataDirectory
        {
            get
            {
                if (OperatingSystem.IsMacOS())
                {
                    return GetMacOSApplicationSupportDirectory();
                }

                var path = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrEmpty(path))
                {
                    path = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? string.Empty;
                }

                return path;
            }
        }

        private static string GetMacOSApplicationSupportDirectory()
        {
            var home = Environment.GetEnvironmentVariable("HOME");
            return string.IsNullOrEmpty(home)
                ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                : Path.Combine(home, "Library", "Application Support");
        }

        /// <summary>
        /// 类 Unix 下执行命令用的 shell。
        /// 用**登录** shell 的原因:GUI 进程继承的 PATH 通常只有 /usr/bin:/bin:/usr/sbin:/sbin,
        /// 而 node/npm/dsh 常装在 /usr/local/bin、/opt/homebrew/bin,或由 ~/.zprofile 追加,
        /// 必须由登录 shell 重新装配 PATH 才找得到。
        /// 注意:.zshrc(nvm 安装器写入 PATH 的地方)只在**交互式** shell 加载,因此这里用 -lc;
        /// 若某用户环境只在 .zshrc 里配 PATH,把 <see cref="ShellLoginArgs"/> 改为 "-ilc" 即可。
        /// </summary>
        private static readonly string UnixShellPath = ResolveUnixShellPath();

        /// <summary>登录 shell 的参数(Windows 不用)。</summary>
        private const string ShellLoginArgs = "-lc";

        private static string ResolveUnixShellPath()
        {
            if (File.Exists("/bin/zsh"))
            {
                return "/bin/zsh";
            }

            return File.Exists("/bin/bash") ? "/bin/bash" : "/bin/sh";
        }

        /// <summary>
        /// 把一条完整命令行包装成 shell 调用,并按要求设置重定向。
        /// Windows:<c>cmd.exe /c &lt;command&gt;</c>(与改造前完全一致)。
        /// 类 Unix:<c>/bin/zsh -lc &lt;command&gt;</c>;命令行通过 ArgumentList 逐个传给子进程,
        /// 不让 .NET 对字符串做二次解析(引号、反斜杠都不会被吃掉)。
        /// </summary>
        public static ProcessStartInfo CreateShellStartInfo(
            string command,
            bool redirectOutput = false,
            bool redirectInput = false)
        {
            var psi = new ProcessStartInfo
            {
                FileName = IsWindows ? "cmd.exe" : UnixShellPath,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            if (IsWindows)
            {
                psi.Arguments = "/c " + command;
            }
            else
            {
                psi.ArgumentList.Add(ShellLoginArgs);
                psi.ArgumentList.Add(command);
            }

            if (redirectOutput)
            {
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
            }

            if (redirectInput)
            {
                psi.RedirectStandardInput = true;
            }

            return psi;
        }

        /// <summary>
        /// 拼出"执行某个可执行文件(带参数)"的命令行文本。
        /// Windows 需要 <c>call</c>:npm 装出来的是 dsh.cmd,必须经 cmd 解释,且 call 能让 cmd 等待其结束;
        /// 类 Unix 直接用引号包裹的完整路径(npm 装的是无扩展名符号链接/带 shebang 的脚本,可直接执行)。
        /// </summary>
        public static string ShellCommandForExecutable(string executablePath, string arguments)
        {
            var command = $"{Quote(executablePath)} {arguments}";
            return IsWindows ? "call " + command : command;
        }

        /// <summary>
        /// 给单个值加引号。Windows 用双引号;类 Unix 用单引号,内部单引号按 POSIX 规则转义为 <c>'\''</c>。
        /// </summary>
        public static string Quote(string value)
            => IsWindows ? $"\"{value}\"" : "'" + value.Replace("'", "'\\''") + "'";

        /// <summary>
        /// 探测命令所在路径的命令行。
        /// Windows:<c>where</c>;类 Unix:<c>command -v</c>(POSIX shell 内建,不依赖 which 是否存在)。
        /// </summary>
        public static string LocateCommandLine(string command)
            => IsWindows ? $"where {command}" : $"command -v {command}";

        /// <summary>
        /// 在系统文件管理器中定位并选中指定文件。
        /// Windows:<c>explorer.exe /select,"path"</c>;macOS:<c>open -R path</c>;
        /// 其他 Unix 无统一"选中"语义,退化为打开所在目录。
        /// </summary>
        public static ProcessStartInfo CreateRevealInFileManagerStartInfo(string filePath)
        {
            if (IsWindows)
            {
                return new ProcessStartInfo("explorer.exe", $"/select,{Quote(filePath)}")
                {
                    UseShellExecute = true,
                };
            }

            if (OperatingSystem.IsMacOS())
            {
                // Finder:open -R <file>,选中该文件
                var reveal = new ProcessStartInfo("open")
                {
                    UseShellExecute = false,
                };
                reveal.ArgumentList.Add("-R");
                reveal.ArgumentList.Add(filePath);
                return reveal;
            }

            // 其他 Unix(如 Linux)没有统一的“选中文件”语义:退化为打开所在目录
            var openDirectory = new ProcessStartInfo("xdg-open")
            {
                UseShellExecute = false,
            };
            openDirectory.ArgumentList.Add(Path.GetDirectoryName(filePath) ?? filePath);
            return openDirectory;
        }
    }
}
