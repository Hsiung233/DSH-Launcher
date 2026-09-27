using System;
using DSH_Launcher.Models;
using DSH_Launcher.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DSH_Launcher.Tests;

/// <summary>
/// 开机自启动:**格式知识**(注册表值 / plist / .desktop 的写法)、命令行标记的识别、
/// 以及设置页状态提示的文案。
/// <para>
/// 为什么值得测:这些字符串是三个平台各自的**契约**,写错了不会编译失败 ——
/// 只会表现为“用户勾了自启动,登录后什么都没发生”,而那种问题在 Windows 上要等到下次登录才暴露。
/// 写入/删除系统侧的代码本身依赖真实注册表,不在单元测试范围内,只在这里盯住它的输出内容。
/// </para>
/// </summary>
[TestClass]
public sealed class AutoStartTests
{
    /// <summary>测试用的可执行文件路径(故意带空格:不引号就会被拆成两段,是这里最容易出的错)。</summary>
    private const string ExePath = @"C:\Program Files\DSH Launcher\DSH Launcher.exe";

    [TestMethod]
    public void WindowsCommand_QuotesPathAndCarriesTheAutoStartSwitch()
    {
        Assert.AreEqual(
            @"""C:\Program Files\DSH Launcher\DSH Launcher.exe"" --autostart",
            AutoStartEntry.WindowsCommand(ExePath));
    }

    [TestMethod]
    public void WindowsCommand_RoundTripsBackToTheSamePath()
    {
        var parsed = AutoStartEntry.ParseWindowsCommand(AutoStartEntry.WindowsCommand(ExePath));

        Assert.AreEqual(ExePath, parsed);
        Assert.IsTrue(AutoStartEntry.IsCurrentTarget(parsed, ExePath));
    }

    [TestMethod]
    public void ParseWindowsCommand_HandlesUnquotedAndMalformedValues()
    {
        // 未加引号(用户手写的常见形态):取到第一个空格为止
        Assert.AreEqual(@"C:\dsh.exe", AutoStartEntry.ParseWindowsCommand(@"C:\dsh.exe --autostart"));

        // 只有开引号没有闭引号:整段当路径,而不是丢掉
        Assert.AreEqual("C:", AutoStartEntry.ParseWindowsCommand("\"C:"));

        // 空值 = 没注册
        Assert.IsNull(AutoStartEntry.ParseWindowsCommand(null));
        Assert.IsNull(AutoStartEntry.ParseWindowsCommand("   "));
    }

    [TestMethod]
    public void IsCurrentTarget_IgnoresCaseButStillDetectsAnotherInstall()
    {
        Assert.IsTrue(AutoStartEntry.IsCurrentTarget(@"C:\APP\DSH LAUNCHER.EXE", @"c:\app\dsh launcher.exe"));
        Assert.IsFalse(AutoStartEntry.IsCurrentTarget(@"D:\old\DSH Launcher.exe", ExePath));
        Assert.IsFalse(AutoStartEntry.IsCurrentTarget(null, ExePath));
    }

    [TestMethod]
    public void MacOSLaunchAgentPlist_RunsAtLoadWithTheAutoStartArgument()
    {
        var plist = AutoStartEntry.MacOSLaunchAgentPlist("/Applications/DSH Launcher.app/Contents/MacOS/DSH Launcher");

        StringAssert.Contains(plist, "<key>RunAtLoad</key>");
        StringAssert.Contains(plist, "<string>--autostart</string>");
        StringAssert.Contains(plist, AutoStartEntry.MacOSLaunchAgentLabel);
        StringAssert.EndsWith(plist, "</plist>\n");
    }

    [TestMethod]
    public void MacOSLaunchAgentPlist_EscapesXmlSpecialCharactersInThePath()
    {
        var plist = AutoStartEntry.MacOSLaunchAgentPlist("/tmp/a&b<c>/DSH Launcher");

        // & 必须先转义,否则会把后面替换出来的 &lt; 二次转义
        StringAssert.Contains(plist, "/tmp/a&amp;b&lt;c&gt;/DSH Launcher");
    }

    [TestMethod]
    public void LinuxDesktopEntry_DescribesAnAutostartApplication()
    {
        var entry = AutoStartEntry.LinuxDesktopEntry("/opt/dsh launcher/DSH Launcher");

        StringAssert.StartsWith(entry, "[Desktop Entry]");
        StringAssert.Contains(entry, "Exec=\"/opt/dsh launcher/DSH Launcher\" --autostart");
        StringAssert.Contains(entry, "X-GNOME-Autostart-enabled=true");
    }

    [TestMethod]
    public void ContainsAutoStart_AcceptsOnlyTheExactSwitch()
    {
        Assert.IsTrue(StartupArguments.ContainsAutoStart(new[] { "--autostart" }));
        Assert.IsTrue(StartupArguments.ContainsAutoStart(new[] { "--profile", "--AUTOSTART" }));

        // 必须是整段相等:别的前缀相同参数不能被误认成自启动
        Assert.IsFalse(StartupArguments.ContainsAutoStart(new[] { "--autostart-foo" }));
        Assert.IsFalse(StartupArguments.ContainsAutoStart(Array.Empty<string>()));
    }

    [TestMethod]
    public void DescribeHint_TellsWhetherTheSettingAndTheSystemAgree()
    {
        var registered = new AutoStartStatus(AutoStartState.Registered, ExePath);

        Assert.AreEqual("系统自启动项:已注册,指向当前程序", AutoStartService.DescribeHint(registered, true));
        StringAssert.Contains(AutoStartService.DescribeHint(registered, false), "下次启动应用时会清除");

        var otherPath = new AutoStartStatus(AutoStartState.RegisteredOtherPath, @"D:\old\DSH Launcher.exe");
        var hint = AutoStartService.DescribeHint(otherPath, true);
        StringAssert.Contains(hint, @"D:\old\DSH Launcher.exe");
        StringAssert.Contains(hint, "下次启动应用时会更新");

        StringAssert.Contains(
            AutoStartService.DescribeHint(new AutoStartStatus(AutoStartState.NotRegistered, null), true),
            "下次启动应用时会补写");
        Assert.AreEqual(
            "系统自启动项:未注册",
            AutoStartService.DescribeHint(new AutoStartStatus(AutoStartState.NotRegistered, null), false));

        // 注册不了的时候必须说清原因,而不是留一句“未注册”让用户反复拨开关
        Assert.AreEqual(
            AutoStartService.UnsupportedReason,
            AutoStartService.DescribeHint(new AutoStartStatus(AutoStartState.Unsupported, null), true));
    }

    [TestMethod]
    public void Decide_KeepsTheSystemSideInSyncWithTheSetting()
    {
        // 设置开着:缺就补、指错了就更正、已经对了就别再动注册表
        Assert.AreEqual(AutoStartAction.Write, AutoStartService.Decide(true, AutoStartState.NotRegistered));
        Assert.AreEqual(AutoStartAction.Write, AutoStartService.Decide(true, AutoStartState.RegisteredOtherPath));
        Assert.AreEqual(AutoStartAction.None, AutoStartService.Decide(true, AutoStartState.Registered));
        Assert.AreEqual(AutoStartAction.Unsupported, AutoStartService.Decide(true, AutoStartState.Unsupported));

        // 设置关着:系统里只要还留着东西就得清掉 —— **包括指向别处的旧注册项**
        // (第一版正是在这里把 RegisteredOtherPath 漏成了“不用管”,残留项永远清不掉)
        Assert.AreEqual(AutoStartAction.Delete, AutoStartService.Decide(false, AutoStartState.Registered));
        Assert.AreEqual(AutoStartAction.Delete, AutoStartService.Decide(false, AutoStartState.RegisteredOtherPath));
        Assert.AreEqual(AutoStartAction.None, AutoStartService.Decide(false, AutoStartState.NotRegistered));
        Assert.AreEqual(AutoStartAction.None, AutoStartService.Decide(false, AutoStartState.Unsupported));
    }

    [TestMethod]
    public void ResolveExecutablePath_RefusesTheDotnetHostItself()
    {
        // 以 dotnet 主机启动时(如 dotnet run)注册自启动毫无意义:登录后只会拉起一个空主机
        Assert.IsNull(AutoStartService.ResolveExecutablePath(@"C:\Program Files\dotnet\dotnet.exe"));
        Assert.IsNull(AutoStartService.ResolveExecutablePath(null));
        Assert.IsNull(AutoStartService.ResolveExecutablePath(string.Empty));

        Assert.AreEqual(ExePath, AutoStartService.ResolveExecutablePath(ExePath));
    }

    [TestMethod]
    public void Settings_AutoStartOnLogonDefaultsToOff()
    {
        Assert.IsFalse(new AppSettings().AutoStartOnLogon);
    }
}
