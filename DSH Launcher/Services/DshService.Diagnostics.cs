using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace DSH_Launcher.Services
{
    /// <summary>
    /// <see cref="DshService"/> 的"启动失败诊断"部分。
    /// <para>
    /// 为什么单独成文件:这一块(失败输出 → 可操作提示 → 依赖层快照留证)只在**失败路径**上跑,
    /// 与进程生命周期、版本管理完全没有耦合,却有 400 行左右;
    /// 放在主文件里会把"启动/停止"的主线淹没在诊断细节中。
    /// </para>
    /// </summary>
    public sealed partial class DshService
    {
        /// <summary>
        /// 分析启动失败的输出,识别常见错误模式并给出可操作的建议。
        /// </summary>
        private static string AnalyzeFailureOutput(string output)
        {
            var hints = new List<string>();

            // 端口被占用(EADDRINUSE / Windows 套接字提示),尝试提取端口号
            var portMatch = Regex.Match(
                output,
                @"EADDRINUSE[^0-9]*(\d+)|address already in use[^0-9]*(\d+)|套接字地址[^0-9]*(\d+)",
                RegexOptions.IgnoreCase);
            if (portMatch.Success)
            {
                var port = portMatch.Groups.Values.Skip(1).FirstOrDefault(g => g.Success && g.Value.Length > 0)?.Value;
                if (!string.IsNullOrEmpty(port))
                {
                    hints.Add($"端口 {port} 已被占用:可能已有一个 dsh 实例正在运行。"
                        + "可先点击\"停止\"按钮,或在任务管理器中结束旧的 node.exe 进程。"
                        + $"\r\n  也可运行 netstat -ano | findstr :{port} 查找占用该端口的进程(末列为 PID)。"
                        + "\r\n  如需换端口,可在首页展开项里的“端口”输入框中改成一个空闲端口后重新运行。");
                }
            }

            // 全局包损坏/缺失
            if (Regex.IsMatch(output, @"Cannot find module|MODULE_NOT_FOUND", RegexOptions.IgnoreCase))
            {
                hints.Add("缺少模块:全局包可能损坏或安装不完整。"
                    + $"\r\n  建议在命令行执行 npm uninstall -g {PackageName} 后重新 npm install -g {PackageName}。");
            }

            // Node 版本不满足
            if (Regex.IsMatch(output, @"Unsupported engine|requires Node|Node\.js v?\d+.*required", RegexOptions.IgnoreCase))
            {
                hints.Add("Node.js 版本不满足该包要求:请升级 Node.js 到包要求的版本后重试。");
            }

            // 权限问题
            if (Regex.IsMatch(output, @"EACCES|Access is denied|拒绝访问|EPERM", RegexOptions.IgnoreCase))
            {
                hints.Add("权限不足:请尝试以管理员身份运行 DSH Launcher,或检查相关文件/端口的安全策略。");
            }

            // 配置文件损坏
            if (Regex.IsMatch(output, @"Unexpected token|SyntaxError.*JSON|Failed to parse", RegexOptions.IgnoreCase))
            {
                hints.Add("配置文件可能损坏或格式错误:请检查 dsh 的配置文件(通常是 JSON)是否合法。");
            }

            if (hints.Count == 0)
            {
                hints.Add("未识别到常见错误模式,请查看下方完整输出定位问题。");
            }

            return string.Join("\r\n", hints.Select(h => "• " + h));
        }

        /// <summary>dsh 报错里 "imported from &lt;目录&gt;" 的目录(即 dsh 的 profile 目录)。</summary>
        private static readonly Regex ProfileImportDirRegex =
            new(@"imported from ([A-Za-z]:\\[^\r\n]+)", RegexOptions.Compiled);

        /// <summary>是否为"profile 依赖解析失败"(整片 Cannot find package / ERR_MODULE_NOT_FOUND)。</summary>
        private static bool IsProfileResolutionFailure(string? output)
            => !string.IsNullOrEmpty(output)
               && (output.Contains("Cannot find package", StringComparison.Ordinal)
                   || output.Contains("ERR_MODULE_NOT_FOUND", StringComparison.Ordinal));

        /// <summary>从失败输出里取出 dsh 的 profile 目录(用于提示里给出具体路径)。</summary>
        private static string? TryGetProfileDirFromOutput(string? output)
        {
            if (string.IsNullOrEmpty(output))
            {
                return null;
            }

            var match = ProfileImportDirRegex.Match(output);
            if (!match.Success)
            {
                return null;
            }

            var profileDir = match.Groups[1].Value.TrimEnd('\\', ' ', '\t', '\r', '\n', ',');
            try
            {
                return Directory.Exists(profileDir) ? profileDir : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// "profile 依赖解析失败"(整片 <c>Cannot find package</c>)时的可操作提示。
        /// <para>
        /// 真因(2026-09-18 端到端实验闭环):处于**上游进程链**里的实例(被别的进程当子进程拉起,
        /// 典型来源是安装器的"安装完成后启动",第三方部署工具同理),Windows 在加载器层随链继承的
        /// AppCompat shim 让 dsh 对 profile 依赖的 junction 大面积不可达;清环境变量拦不住。
        /// 正常路径已由启动时的"逃逸上游进程链"处理(见 App.axaml.cs,它不依赖安装器侧的任何修复),
        /// 所以这里只描述现象并给出下一步。
        /// </para>
        /// </summary>
        private static string ProfileResolutionHintFor(string output)
        {
            if (!IsProfileResolutionFailure(output))
            {
                return string.Empty;
            }

            var profileDir = TryGetProfileDirFromOutput(output) ?? @"%USERPROFILE%\.dsh\profiles";
            return "• dsh 解析不到自己的 profile 依赖(整片 \"Cannot find package\"):"
                + "已确认的成因是**上游进程链**带来的链级兼容层标记(进程内 AppCompat shim 随链继承),"
                + "它会让 dsh 看不到 profile 依赖目录里的链接。\r\n"
                + "  本程序启动时会自动逃逸出该进程链并重开自己;看到这条提示说明当前实例仍在链里 —— "
                + "请关闭本程序后改用开始菜单/桌面图标重新打开(不要从安装器、部署工具等父进程里拉起它)。\r\n"
                + $"  诊断快照已写入日志文件;profile 目录:{profileDir}。\r\n";
        }

        /// <summary>
        /// 快照里每个包名的展示上限(超出截断,防 app.log 失控)。
        /// </summary>
        private const int DiagnosticsPackageLimit = 20;

        /// <summary>dsh 失败输出里的 "Cannot find package '&lt;包名&gt;'"。</summary>
        private static readonly Regex MissingPackageRegex =
            new(@"Cannot find package '([^']+)'", RegexOptions.Compiled);

        /// <summary>
        /// "profile 依赖解析失败"发生时,对 dsh 的各依赖层拍快照并写入 app.log。
        /// <para>
        /// 为什么要快照(2026-09-18 排查结论):dsh 插件树的导入解析发生在加载器自身位置
        /// (全局包 <c>node_modules\@deepseek-ai\*</c> 内部),而失败消息里的
        /// "imported from …\profiles\web\" 只是错误拼装的显示基,不是真实解析路径;
        /// 共享农场(<c>profiles\node_modules</c>,dsh 自建 junction)与 profile 的 pnpm 层
        /// 都会在每次启动时被 dsh 自愈。因此失败时点四层里必有一层处于残缺,当场留证是唯一定位手段。
        /// 各层独立 try/catch:一层失败不影响其余层留证。任何异常只记录、不抛出。
        /// </para>
        /// </summary>
        private async Task CaptureResolutionDiagnosticsAsync(string failureOutput)
        {
            try
            {
                var lines = new List<string>
                {
                    $"[启动诊断] dsh 依赖层快照 {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}(解析失败当刻)"
                };

                // 0) 失败输出里缺失的包名(去重截断)
                var missing = MissingPackageRegex.Matches(failureOutput)
                    .Select(m => m.Groups[1].Value)
                    .Where(name => !string.IsNullOrEmpty(name))
                    .Distinct(StringComparer.Ordinal)
                    .Take(DiagnosticsPackageLimit)
                    .ToList();
                lines.Add(missing.Count > 0
                    ? $"失败输出中缺失的包({missing.Count} 个):{string.Join(", ", missing)}"
                    : "失败输出中未提取到“Cannot find package 'X'”形式的包名。");

                // ① 全局包(=加载器解析位,决定性一层):由 shim 反推 npm bin 目录
                string? globalDsh = null;
                try
                {
                    var (shim, _) = await FindDshShimAsync();
                    globalDsh = this.TryGetGlobalDshDirFromShim(shim);
                }
                catch (Exception)
                {
                    // 定位失败保持 null
                }

                if (globalDsh is null)
                {
                    lines.Add("① 全局包:未定位到(shim 推导失败或未安装)。");
                }
                else
                {
                    lines.Add($"① 全局包(加载器解析位):{globalDsh}");
                    var internalNm = Path.Combine(globalDsh, "node_modules");
                    lines.Add($"  内部 node_modules 总条目:{CountEntries(internalNm)},@deepseek-ai 下:{CountEntries(Path.Combine(internalNm, "@deepseek-ai"))}");

                    try
                    {
                        var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(globalDsh, "package.json"))).RootElement;
                        var declared = manifest.TryGetProperty("dependencies", out var deps)
                            ? deps.EnumerateObject().Select(p => p.Name).ToList()
                            : new List<string>();
                        var broken = declared.Where(dep => !DirReachable(Path.Combine(internalNm, dep))).ToList();
                        lines.Add(broken.Count > 0
                            ? $"  ⚠ 声明依赖 {declared.Count} 项中有 {broken.Count} 项在全局 node_modules 解析不到:{string.Join(", ", broken.Take(DiagnosticsPackageLimit))}"
                            : $"  声明依赖 {declared.Count} 项全部可解析。");
                    }
                    catch (Exception ex)
                    {
                        lines.Add($"  读取全局 package.json 失败:{ex.Message}");
                    }

                    // 失败包在全局层的逐项可达性(= dsh 插件加载时的真实解析位)
                    foreach (var pkg in missing)
                    {
                        lines.Add($"  {pkg} → 全局层:{ReachabilityAt(internalNm, pkg)}");
                    }
                }

                // ② 共享农场(仅留证,dsh 每次启动会自愈)
                var profileDir = TryGetProfileDirFromOutput(failureOutput)
                    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh", "profiles", ProfileName);
                var profilesRoot = Path.GetDirectoryName(profileDir.TrimEnd(Path.DirectorySeparatorChar));
                var farmDir = profilesRoot is null ? null : Path.Combine(profilesRoot, "node_modules");
                if (farmDir is null || !Directory.Exists(farmDir))
                {
                    lines.Add($"② 共享农场:{farmDir ?? "(未知)"} 不存在。");
                }
                else
                {
                    var dangling = FindDanglingLinks(farmDir);
                    lines.Add($"② 共享农场:{farmDir} / 共 {CountEntries(farmDir)} 项,悬空 junction {dangling.Count} 项{(dangling.Count > 0 ? ":" + string.Join(", ", dangling.Take(DiagnosticsPackageLimit)) : string.Empty)}");
                    foreach (var pkg in missing.Take(3))
                    {
                        lines.Add($"  {pkg} → 农场:{ReachabilityAt(farmDir, pkg)}");
                    }
                }

                // ③ profile 自身 node_modules(pnpm 层)+ ④ .dsh-module-fallback(dsh 私有兜底)
                var webNm = Path.Combine(profileDir, "node_modules");
                lines.Add($"③ profile node_modules:{webNm}(共 {CountEntries(webNm)} 项)");
                var webDangling = Directory.Exists(webNm) ? FindDanglingLinks(webNm) : new List<string>();
                if (webDangling.Count > 0)
                {
                    lines.Add($"  ⚠ 悬空链接 {webDangling.Count} 项:{string.Join(", ", webDangling.Take(DiagnosticsPackageLimit))}");
                }
                foreach (var pkg in missing.Take(3))
                {
                    lines.Add($"  {pkg} → web 层:{ReachabilityAt(webNm, pkg)}");
                }

                var fallbackDir = Path.Combine(profileDir, ".dsh-module-fallback", "node_modules");
                lines.Add($"④ .dsh-module-fallback:{fallbackDirDesc(fallbackDir)}");

                AppLogService.Write(string.Join("\r\n", lines));
            }
            catch (Exception ex)
            {
                // 快照本身绝不能打断启动/重试流程
                AppLogService.Write($"[启动诊断] 快照失败:{ex.Message}");
            }

            static string fallbackDirDesc(string dir) =>
                Directory.Exists(dir) ? $"{dir}(共 {CountEntries(dir)} 项)" : $"{dir} 不存在";
        }

        /// <summary>由 npm shim 路径反推全局包目录(&lt;bin&gt;\node_modules\@deepseek-ai\dsh);仅 Windows 形态(dsh.cmd),失败返回 null。</summary>
        private string? TryGetGlobalDshDirFromShim(string shimPath)
        {
            try
            {
                if (string.IsNullOrEmpty(shimPath))
                {
                    return null;
                }

                var binDir = Path.GetDirectoryName(shimPath);
                if (binDir is null)
                {
                    return null;
                }

                var candidate = Path.Combine(binDir, "node_modules", "@deepseek-ai", "dsh");
                return Directory.Exists(candidate) ? candidate : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>目录内条目数(直接子项);目录不存在或不可枚举返回 -1。</summary>
        private static int CountEntries(string? dir)
        {
            try
            {
                if (dir is null || !Directory.Exists(dir))
                {
                    return -1;
                }

                var count = 0;
                using var enumerator = new DirectoryInfo(dir).EnumerateFileSystemInfos().GetEnumerator();
                while (enumerator.MoveNext())
                {
                    count++;
                }

                return count;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        /// <summary>
        /// 目录是否"可达":存在、可进入、且像一个包(含 package.json;无 package.json 但可枚举也算存在,
        /// 避免 @scope 真目录被误报)。
        /// </summary>
        private static bool DirReachable(string dir)
        {
            try
            {
                if (!Directory.Exists(dir))
                {
                    return false;
                }

                var di = new DirectoryInfo(dir);
                if (File.Exists(Path.Combine(dir, "package.json")))
                {
                    return true;
                }

                using var e = di.EnumerateFileSystemInfos().GetEnumerator();
                return e.MoveNext();
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>包名在某一 node_modules 层的可解析性描述:✓可解析 / ⚠悬空 / ✗不存在 / ?枚举失败。</summary>
        private static string ReachabilityAt(string nodeModulesDir, string packageName)
        {
            try
            {
                if (nodeModulesDir is null || !Directory.Exists(nodeModulesDir))
                {
                    return "层不存在";
                }

                var link = Path.Combine(nodeModulesDir, packageName.Replace('/', Path.DirectorySeparatorChar));
                var di = new DirectoryInfo(link);
                if (!di.Exists)
                {
                    return "✗ 不存在";
                }

                var reparse = (di.Attributes & FileAttributes.ReparsePoint) != 0;
                if (File.Exists(Path.Combine(link, "package.json")))
                {
                    return reparse ? "✓ 可解析(junction,目标可达)" : "✓ 可解析(真目录)";
                }

                // 悬空 junction 不能再枚举(枚举会抛异常):直接判定
                if (reparse)
                {
                    return "⚠ 悬空链接(目标不可达)";
                }

                using var e = di.EnumerateFileSystemInfos().GetEnumerator();
                return e.MoveNext() ? "⚠ 存在但不可达(枚举到内容却读不到 package.json)" : "⚠ 存在但为空目录";
            }
            catch (Exception ex)
            {
                return $"? 枚举失败({ex.Message})";
            }
        }

        /// <summary>
        /// 扫描一个 node_modules 下的悬空 junction/符号链接(顶层与 @scope 一层)。
        /// 判定:reparse 条目 + 内部 package.json 不可达 = 悬空(用 cmd rmdir 类工具可安全清掉,不影响目标)。
        /// 枚举失败跳过,不抛出。
        /// </summary>
        private static List<string> FindDanglingLinks(string nodeModulesDir)
        {
            var dangling = new List<string>();
            try
            {
                foreach (var entry in new DirectoryInfo(nodeModulesDir).EnumerateDirectories())
                {
                    try
                    {
                        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            if (!File.Exists(Path.Combine(entry.FullName, "package.json")))
                            {
                                dangling.Add(entry.Name);
                            }

                            continue;
                        }

                        if (!entry.Name.StartsWith("@", StringComparison.Ordinal))
                        {
                            continue;
                        }

                        foreach (var child in entry.EnumerateDirectories())
                        {
                            try
                            {
                                if ((child.Attributes & FileAttributes.ReparsePoint) != 0
                                    && !File.Exists(Path.Combine(child.FullName, "package.json")))
                                {
                                    dangling.Add($"{entry.Name}/{child.Name}");
                                }
                            }
                            catch (Exception)
                            {
                                // 单条目失败忽略
                            }
                        }
                    }
                    catch (Exception)
                    {
                        // 单条目失败忽略
                    }
                }
            }
            catch (Exception)
            {
                // 整层不可枚举时返回已收集的部分
            }

            return dangling;
        }
    }
}
