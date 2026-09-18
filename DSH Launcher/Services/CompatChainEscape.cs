using System;
using System.Diagnostics;
using System.IO;

namespace DSH_Launcher.Services
{
    /// <summary>逃逸标记文件的状态。</summary>
    internal enum EscapeMarkerState
    {
        /// <summary>没有标记文件(不是逃逸拉起的实例)。</summary>
        None,

        /// <summary>标记新鲜 ⇒ 本实例就是刚被逃逸拉起的,不能再逃逸(循环护栏)。</summary>
        Fresh,

        /// <summary>标记已过期 ⇒ 上一次逃逸留下的残留,按"没有标记"处理。</summary>
        Stale,
    }

    /// <summary>
    /// "上游进程链逃逸":凡是被别的进程当子进程拉起的实例(安装器的"安装完成后启动"是典型来源,
    /// 第三方/企业部署工具、将来可能的自动更新器同理),无论环境变量怎么清理、cwd/令牌如何,
    /// dsh 服务启动必败(整片 <c>Cannot find package</c>),而同一 exe 手动启动必成 ——
    /// Windows 在加载器层随**进程链**继承的 AppCompat shim 才是元凶,清环境变量拦不住。
    /// 唯一可靠解法:由 explorer.exe(干净链根)重新拉起自己,自己退出。
    /// <para>
    /// ⚠ 这是**独立于安装器的通用自愈**,不是 <c>installer.iss</c> 那条 [Run] 的补丁:
    /// 安装器改走 explorer 修的是"正常路径不再产生这种链",本条管的是"任何链都能自愈" ——
    /// 不要因为安装器修好了就判定它是死代码而删除(别的部署方式照样会把本程序放进自己的进程链)。
    /// </para>
    /// <para>
    /// 循环护栏:<c>__COMPAT_LAYER</c> 若被**持久化**(用户/软件写成 HKCU\Environment 变量),
    /// explorer 拉起的新实例照样非空 —— 没有护栏就会无限重启。因此逃逸前写标记文件,
    /// 被拉起的实例首次启动时消费一次;标记过期视为陈旧残留,照常允许逃逸。
    /// </para>
    /// </summary>
    internal static class CompatChainEscape
    {
        /// <summary>逃逸标记文件路径(与 app.log 同目录)。文件名沿用历史命名,作为稳定标识保持不改。</summary>
        public static string MarkerPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DSH Launcher", "Settings", "installer-escape.marker");

        /// <summary>
        /// 标记文件第二行的字面量,表示"触发逃逸的那次启动是自启动拉起的"。
        /// <para>
        /// ⚠ 逃逸重启经 explorer.exe,**不能带命令行参数**(explorer 会把多余参数当"要打开的对象")。
        /// 但自启动语义不能因此丢掉 —— 否则"持久 <c>__COMPAT_LAYER</c> 环境 + 自启动"的组合下,
        /// 逃逸后的新实例会按 <c>ShowMainWindowOnStartup</c> 弹主界面,违背自启动"静默进托盘"的意图。
        /// 于是把标记写进标记文件(它本来就是写给新实例看的),新实例消费标记时接回
        /// <see cref="StartupArguments.MarkAutoStartLaunch"/>。
        /// </para>
        /// </summary>
        private const string AutoStartMarkerLine = "autostart";

        /// <summary>标记有效期:超过它的残留视为陈旧(照常允许逃逸)。</summary>
        private static readonly TimeSpan MarkerLifetime = TimeSpan.FromMinutes(2);

        /// <summary>
        /// 当前进程是否处于"上游进程链"里(以 <c>__COMPAT_LAYER</c> 非空为信号),即被别的进程当作子进程拉起。
        /// <para>
        /// 判定依据(2026-09-18 端到端实验):安装器"安装完成后启动"拉起的实例,环境里必有非空
        /// <c>__COMPAT_LAYER</c>(实测见过 <c>DetectorsAppHealth</c>/<c>ElevateCreateProcess</c> 两种,
        /// 随上游进程本身被哪个进程拉起而异),而手动/explorer 启动永远为空;此类实例的 dsh 服务启动必败
        /// (链级标记,清环境变量无效),所以一律经 explorer.exe 逃逸重启。普通用户不会手动给本程序设
        /// <c>__COMPAT_LAYER</c>(右键兼容性设置写入的是 HKCU\...\AppCompatFlags\Layers,不走环境变量)。
        /// </para>
        /// <para>
        /// ⚠ 判定条件**只看"非空",不要按值收窄**(不能只认 <c>ElevateCreateProcess</c> 之类):
        /// 实测链上的值随上游进程变化,按值匹配会把已修复的场景重新放进坑里。误判(如被写成持久环境变量)
        /// 的代价只是"多重启一次",已由 <see cref="ConsumeMarker"/> 的护栏与提示兑付。
        /// </para>
        /// </summary>
        public static bool IsInsideInheritedCompatChain()
        {
            try
            {
                var layer = Environment.GetEnvironmentVariable(ChildEnvironment.CompatibilityLayerVariable);
                return !string.IsNullOrWhiteSpace(layer);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 消费逃逸标记:标记存在且新鲜 ⇒ 返回 <see cref="EscapeMarkerState.Fresh"/>(调用方**不要**再逃逸);
        /// 不存在 ⇒ <see cref="EscapeMarkerState.None"/>;已过期 ⇒ <see cref="EscapeMarkerState.Stale"/>。
        /// 无论哪种情况都会把文件删掉(避免残留影响下一次启动)。
        /// </summary>
        public static EscapeMarkerState ConsumeMarker()
        {
            try
            {
                var path = MarkerPath;
                if (!File.Exists(path))
                {
                    return EscapeMarkerState.None;
                }

                var content = File.ReadAllText(path);
                var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(path);
                File.Delete(path);

                // 逃逸前若是自启动拉起,把语义接过来(即使本实例走护栏不再逃逸也要接 —— 它就是"逃逸后重开的实例")
                if (content.Contains(AutoStartMarkerLine, StringComparison.Ordinal))
                {
                    StartupArguments.MarkAutoStartLaunch();
                    AppLogService.Write("[启动] 逃逸前是自启动拉起,重启后维持自启动行为(不弹主界面)。");
                }

                if (age <= MarkerLifetime)
                {
                    AppLogService.Write("[启动] 本实例是刚由进程链逃逸拉起的(逃逸标记有效),不再二次逃逸,直接继续运行。");
                    return EscapeMarkerState.Fresh;
                }

                AppLogService.Write($"[启动] 发现陈旧的逃逸标记(已超过 {MarkerLifetime.TotalMinutes:0} 分钟),忽略并按新逃逸处理。");
                return EscapeMarkerState.Stale;
            }
            catch (Exception)
            {
                // 标记读写失败时按"无标记"处理:宁可逃逸一次,也不冒无限重启的风险
                return EscapeMarkerState.None;
            }
        }

        /// <summary>
        /// 标记文件里是否带着"逃逸前是自启动"的字面量(不消费、不删除文件本体)。
        /// 给**干净链**路径用:那里不调 <see cref="ConsumeMarker"/>(护栏只对链内实例有意义),
        /// 但也要在删掉陈旧标记前把自启动语义接过来。
        /// </summary>
        public static bool MarkerCarriesAutoStartFlag()
        {
            try
            {
                return File.Exists(MarkerPath)
                    && File.ReadAllText(MarkerPath).Contains(AutoStartMarkerLine, StringComparison.Ordinal);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 护栏触发时给用户的一句提示(放在日志面板里)。
        /// 逃逸过一次却仍带着链级标记 ⇒ <c>__COMPAT_LAYER</c> 很可能是**持久**环境变量,
        /// 此时 explorer 拉起的新实例同样被套层、dsh 仍会失败,得让用户知道去哪儿查。
        /// </summary>
        public static string PersistentCompatLayerHint =>
            "环境提示:检测到链级兼容层标记,且本实例已经是逃逸后重开的,因此不再重启。"
            + "若 dsh 启动失败,请检查是否有软件把 __COMPAT_LAYER 写成了持久环境变量"
            + "(用户/系统环境变量)并清除它。";

        /// <summary>
        /// 由 explorer.exe 重新拉起自己以逃逸上游进程链(任何把本程序当子进程拉起的父进程)。
        /// <para>
        /// 返回 true 表示逃逸重启已发起(调用方应退出);false 表示失败(调用方应继续运行,保底旧行为,
        /// 并把单实例锁拿回来 —— 见 <see cref="SingleInstanceGuard"/>)。失败时本方法会清掉刚写的标记,
        /// 否则 2 分钟内的下一次启动会误判"我是逃逸拉起的"而跳过逃逸。
        /// </para>
        /// <para>
        /// ⚠ 调用方必须**先释放单实例锁**:explorer 创建新进程需要时间,若旧实例仍持有互斥体,
        /// 新实例会走"检测到已有实例"路径被误退。explorer 自己是干净的加载器链根,
        /// 由它创建的新实例不继承任何链标记,环境也换成用户会话环境(无兼容层变量)。
        /// </para>
        /// <para>
        /// 已知取舍(有意为之,不要当 bug 修):重启经 explorer **不能带命令行参数**(explorer 会把多余参数
        /// 当"要打开的对象")。命令行参数因此会丢 —— 目前唯一依赖参数的"自启动"语义,
        /// 靠把标记写进逃逸标记文件来承载(见 <see cref="AutoStartMarkerLine"/>),新实例消费标记时接回。
        /// 将来若引入其它必须经命令行传递的启动行为,得另想办法(临时快捷方式/自拼 ShellExecuteEx)。
        /// 工作目录会变成 exe 所在目录 —— 与"手动双击启动"一致,比继承上游 cwd 更可预测。
        /// </para>
        /// </summary>
        public static bool TryRestartViaExplorer(out string failureReason)
        {
            failureReason = string.Empty;
            var exe = Environment.ProcessPath;

            AppLogService.Write("[启动] 检测到上游进程链带来的兼容层标记(__COMPAT_LAYER 非空):"
                + "将由 explorer.exe 以干净环境重新拉起本程序并退出当前实例。");

            try
            {
                if (string.IsNullOrEmpty(exe))
                {
                    failureReason = "无法确定自身可执行文件路径";
                    return false;
                }

                // 写逃逸标记(给拉起的新实例看,见 ConsumeMarker);
                // 本次是自启动拉起时把该语义一并写进去,explorer 重启丢不掉它。
                Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!);
                File.WriteAllText(
                    MarkerPath,
                    DateTime.UtcNow.ToString("O")
                    + (StartupArguments.IsAutoStartLaunch ? $"\n{AutoStartMarkerLine}" : string.Empty));

                // explorer.exe 会把参数当作要打开的对象,对 exe 即是"启动该程序",
                // 新进程的父进程是 explorer(不在上游进程链里)。
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{exe}\"") { UseShellExecute = true });
                return true;
            }
            catch (Exception ex)
            {
                failureReason = ex.Message;
                DeleteMarker();
                return false;
            }
        }

        /// <summary>
        /// 删除逃逸标记(不存在或删除失败都不影响流程)。
        /// 两种场景会用到:刚才逃逸失败要收尾;本次是干净链,上一次逃逸留下的标记已无意义 ——
        /// 不清理的话,它会让 2 分钟内"下一次带链启动"误判为"我就是逃逸拉起的"而跳过逃逸,直接回到必败状态。
        /// </summary>
        public static void DeleteMarker()
        {
            try
            {
                var path = MarkerPath;
                if (File.Exists(path))
                {
                    File.Delete(path);
                    AppLogService.Write("[启动] 已清理逃逸标记。");
                }
            }
            catch (Exception)
            {
                // 标记清理失败不影响流程(它 2 分钟后会自动失效)
            }
        }
    }
}
