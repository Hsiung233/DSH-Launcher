using System;
using DSH_Launcher.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DSH_Launcher.Tests;

/// <summary>
/// 看门狗(服务意外退出自动重启)的纯决策规则。
/// 这些规则写错的表现是"该重启时不动 / 不该重启时反复拉起",不会报错,只能靠测试兜住。
/// </summary>
[TestClass]
public sealed class WatchdogPolicyTests
{
    [TestMethod]
    public void ShouldSchedule_EnabledAndNotUserStopped_AndWithinQuota_ReturnsTrue()
    {
        Assert.IsTrue(WatchdogPolicy.ShouldSchedule(autoRestartEnabled: true, stoppedByUser: false, restartsUsed: 0));
        Assert.IsTrue(WatchdogPolicy.ShouldSchedule(autoRestartEnabled: true, stoppedByUser: false, restartsUsed: WatchdogPolicy.MaxRestarts - 1));
    }

    [TestMethod]
    public void ShouldSchedule_Disabled_OrUserStopped_OrQuotaExhausted_ReturnsFalse()
    {
        Assert.IsFalse(WatchdogPolicy.ShouldSchedule(autoRestartEnabled: false, stoppedByUser: false, restartsUsed: 0));
        Assert.IsFalse(WatchdogPolicy.ShouldSchedule(autoRestartEnabled: true, stoppedByUser: true, restartsUsed: 0));
        Assert.IsFalse(WatchdogPolicy.ShouldSchedule(autoRestartEnabled: true, stoppedByUser: false, restartsUsed: WatchdogPolicy.MaxRestarts));
        Assert.IsFalse(WatchdogPolicy.ShouldSchedule(autoRestartEnabled: true, stoppedByUser: false, restartsUsed: WatchdogPolicy.MaxRestarts + 1));
    }

    [TestMethod]
    public void DelayFor_FollowsBackoffSequence()
    {
        Assert.AreEqual(TimeSpan.FromSeconds(5), WatchdogPolicy.DelayFor(1));
        Assert.AreEqual(TimeSpan.FromSeconds(15), WatchdogPolicy.DelayFor(2));
        Assert.AreEqual(TimeSpan.FromSeconds(30), WatchdogPolicy.DelayFor(3));
    }

    /// <summary>越界输入不能抛异常:调用方即使算错次数也只是退回序列端点。</summary>
    [TestMethod]
    public void DelayFor_OutOfRange_ClampsToSequence()
    {
        Assert.AreEqual(TimeSpan.FromSeconds(5), WatchdogPolicy.DelayFor(0));
        Assert.AreEqual(TimeSpan.FromSeconds(30), WatchdogPolicy.DelayFor(99));
    }

    [TestMethod]
    public void ShouldResetAttempts_AtThresholdOrLonger()
    {
        Assert.IsFalse(WatchdogPolicy.ShouldResetAttempts(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1)));
        Assert.IsTrue(WatchdogPolicy.ShouldResetAttempts(TimeSpan.FromMinutes(5)));
        Assert.IsTrue(WatchdogPolicy.ShouldResetAttempts(TimeSpan.FromHours(1)));
    }

    /// <summary>设置页说明文案里写死了次数与间隔,这里防两者漂移。</summary>
    [TestMethod]
    public void DescribeBackoff_MentionsEveryDelay()
    {
        var text = WatchdogPolicy.DescribeBackoff();
        Assert.IsTrue(text.Contains("5", StringComparison.Ordinal));
        Assert.IsTrue(text.Contains("15", StringComparison.Ordinal));
        Assert.IsTrue(text.Contains("30", StringComparison.Ordinal));
    }
}
