using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

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
        /// “原样字节”编码:每个字节映射成一个字符,字节 ↔ 字符可无损往返。
        /// 用它当下层解码,就能在收到字符串之后再把原始字节还原出来,从而逐行判定真正的编码。
        /// </summary>
        private static readonly Encoding RawByteEncoding = Encoding.Latin1;

        /// <summary>严格 UTF-8:遇到非法字节会抛 <see cref="DecoderFallbackException"/>(判定靠这个)。</summary>
        private static readonly Encoding StrictUtf8 =
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        /// <summary>
        /// 系统 OEM 代码页 —— cmd.exe 等原生工具在**重定向**时使用的编码(中文 Windows = GBK/936)。
        /// 取不到时退回 UTF-8(总比把字符变成替字符好)。
        /// </summary>
        private static readonly Encoding OemEncoding = CreateOemEncoding();

        private static Encoding CreateOemEncoding()
        {
            // ⚠ 必须先注册代码页提供程序:.NET (Core) 起默认的 EncodingProvider 只认 Unicode 系列,
            // 中文 Windows 的 GBK/936 不在其中 —— 不注册时 GetEncoding(936) 会抛
            // NotSupportedException,下面这个 catch 就把 OemEncoding 静默退化成 UTF-8,
            // 于是 DecodeChildOutputLine 的"严格 UTF-8 失败 → 回退 OEM 代码页"分支永远解不出
            // cmd.exe 自己写的中文消息(GBK 字节),只能得到 U+FFFD 乱码。
            // 放在这里而不是静态构造函数:静态字段初始化器(含 OemEncoding)在静态构造函数体之前执行。
            // CodePagesEncodingProvider 由共享框架提供,不需要额外的 NuGet 包。
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            try
            {
                return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
            }
            catch (Exception)
            {
                return Encoding.UTF8;
            }
        }

        /// <summary>
        /// 解码子进程输出的一行。
        /// <para>
        /// ⚠ 不能“一刀切”用某个编码:一次 <c>cmd.exe /c</c> 的输出里可能**混着两种** ——
        /// Node 系工具(dsh / npm / pnpm)写 UTF-8,而 cmd.exe 自己的消息
        /// (如「'xxx' 不是内部或外部命令」)是系统 OEM 代码页;实测 <c>chcp 65001</c>
        /// 对重定向的 cmd 输出**无效**,而且两者还会落在同一管道(甚至同一 stderr)上,无法按流分开。
        /// </para>
        /// <para>
        /// 判据:先按严格 UTF-8 试解。UTF-8 自带合法性校验,而 GBK 的第二字节常落在 0x40-0x7F,
        /// 不是合法的 UTF-8 续字节,所以 GBK 文本几乎必然解失败 → 回退 OEM 代码页。
        /// 纯 ASCII 行两种解一致,不受影响。
        /// </para>
        /// </summary>
        public static string DecodeChildOutputLine(string rawByteChars)
        {
            if (rawByteChars.Length == 0)
            {
                return rawByteChars;
            }

            var bytes = RawByteEncoding.GetBytes(rawByteChars);
            try
            {
                return StrictUtf8.GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                return OemEncoding.GetString(bytes);
            }
        }

        /// <summary>
        /// 多行文本的智能解码。**逐行判定** —— 同一份捕获里可能同时含 cmd 消息与 Node 输出。
        /// </summary>
        public static string DecodeChildOutputText(string rawByteChars)
            => string.Join("\n", rawByteChars.Split('\n').Select(DecodeChildOutputLine));

        /// <summary>
        /// 把一条完整命令行包装成 shell 调用,并按要求设置重定向。
        /// Windows:<c>cmd.exe /c &lt;command&gt;</c>(与改造前完全一致)。
        /// 类 Unix:<c>/bin/zsh -lc &lt;command&gt;</c>;命令行通过 ArgumentList 逐个传给子进程,
        /// 不让 .NET 对字符串做二次解析(引号、反斜杠都不会被吃掉)。
        /// </summary>
        /// <param name="rawByteOutput">
        /// 是否把输出按“原样字节”保留,交给调用方用 <see cref="DecodeChildOutputLine"/> 判定编码。
        /// **输出会被展示或解析的调用都要传 true** —— 否则 .NET 会用系统代码页一刀切,
        /// 把 Node 系的 UTF-8 输出解成乱码(实测 <c>…WARN…</c> → <c>鈥塛ARN鈥?</c>、
        /// <c>—</c> → <c>鈥?</c>、盒线字符 → <c>鈹?/c>)。
        /// <para>只有纯 ASCII 用途(如 <c>pnpm --version</c>)可以不传。</para>
        /// </param>
        public static ProcessStartInfo CreateShellStartInfo(
            string command,
            bool redirectOutput = false,
            bool redirectInput = false,
            bool rawByteOutput = false)
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

                if (rawByteOutput)
                {
                    psi.StandardOutputEncoding = RawByteEncoding;
                    psi.StandardErrorEncoding = RawByteEncoding;
                }
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
