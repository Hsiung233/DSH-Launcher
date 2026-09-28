using System;

namespace DSH_Launcher.Services
{
    /// <summary>
    /// 看门狗(服务意外退出后自动重启)的**纯决策逻辑**,与界面和 <see cref="DshService"/> 的
    /// 运行时状态无关 —— 单独成类是为了能被单元测试盯住(退避序列、次数上限、计数重置这些
    /// "改错了不容易当场发现"的规则)。
    /// <para>
    /// 策略:稳定运行超过 <see cref="StableRunReset"/> 后发生的退出才计入重启序列,
    /// 且每次重启间隔按 5/15/30 秒递增;连续 <see cref="MaxRestarts"/> 次后放弃并通知用户。
    /// 用户手动启停、重启应用、退出程序都不属于"意外退出",永远不触发。
    /// </para>
    /// </summary>
    internal static class WatchdogPolicy
    {
        /// <summary>连续自动重启的最大次数。设置页的说明文案与此保持一致(改动时同步)。</summary>
        public const int MaxRestarts = 3;

        /// <summary>
        /// 稳定运行的重置阈值:本次运行时长达到该值时,重启计数先归零再判定 ——
        /// 长期稳定运行中偶发一次崩溃应从头计数,而不是接着上次的额度继续耗。
        /// </summary>
        public static readonly TimeSpan StableRunReset = TimeSpan.FromMinutes(5);

        /// <summary>各次重启前的等待(第 N 次重启取第 N 项;1 起始)。</summary>
        private static readonly TimeSpan[] Backoff =
        {
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(15),
            TimeSpan.FromSeconds(30),
        };

        /// <summary>
        /// 是否应该安排自动重启。判定输入:设置开关、是否用户主动停止、已用掉的自动重启次数。
        /// (调用方保证只在"非启动失败观察期"的意外退出路径上询问。)
        /// </summary>
        public static bool ShouldSchedule(bool autoRestartEnabled, bool stoppedByUser, int restartsUsed)
            => autoRestartEnabled && !stoppedByUser && restartsUsed < MaxRestarts;

        /// <summary>第 <paramref name="attempt"/> 次(1 起始)重启前的等待时长;超出序列时给最后一次。</summary>
        public static TimeSpan DelayFor(int attempt)
        {
            var index = Math.Clamp(attempt, 1, Backoff.Length) - 1;
            return Backoff[index];
        }

        /// <summary>本次运行时长是否足以把重启计数重置(见 <see cref="StableRunReset"/>)。</summary>
        public static bool ShouldResetAttempts(TimeSpan ranFor) => ranFor >= StableRunReset;

        /// <summary>给设置页说明文案用的退避描述(与 <see cref="Backoff"/> 保持一致)。</summary>
        public static string DescribeBackoff() => "5/15/30 秒";
    }
}
