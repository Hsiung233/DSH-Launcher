using System;
using System.Linq;
using DSH_Launcher.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DSH_Launcher.Tests;

/// <summary>
/// “重复启动应用时”下拉框的选项顺序与枚举值的互转。
/// 动机:界面顺序(无动作 → 打开主界面 → WebView → 浏览器)与 <see cref="WebOpenAction"/>
/// 的枚举值顺序**不同**,这张表一旦和设置页 ComboBoxItem 的书写顺序对不上,
/// 用户选的动作就会静默错位(选“打开浏览器”却显示主界面这种)。
/// </summary>
[TestClass]
public sealed class RepeatLaunchOptionsTests
{
    [TestMethod]
    public void Order_IsTheSettingsPageItemOrder()
    {
        CollectionAssert.AreEqual(
            new[]
            {
                WebOpenAction.None,
                WebOpenAction.MainWindow,
                WebOpenAction.WebView,
                WebOpenAction.Browser,
            },
            RepeatLaunchOptions.Order.ToArray());
    }

    [TestMethod]
    public void Order_CoversEveryActionExactlyOnce()
    {
        var allActions = Enum.GetValues<WebOpenAction>();

        CollectionAssert.AreEquivalent(allActions, RepeatLaunchOptions.Order.ToArray());
        Assert.AreEqual(allActions.Length, RepeatLaunchOptions.Order.Count);
    }

    [TestMethod]
    public void ToIndex_UsesInterfaceOrder_NotEnumValues()
    {
        Assert.AreEqual(0, RepeatLaunchOptions.ToIndex(WebOpenAction.None));
        Assert.AreEqual(1, RepeatLaunchOptions.ToIndex(WebOpenAction.MainWindow));
        Assert.AreEqual(2, RepeatLaunchOptions.ToIndex(WebOpenAction.WebView));
        Assert.AreEqual(3, RepeatLaunchOptions.ToIndex(WebOpenAction.Browser));
    }

    [TestMethod]
    public void RoundTrip_EveryAction_SurvivesIndexMapping()
    {
        foreach (var action in Enum.GetValues<WebOpenAction>())
        {
            Assert.AreEqual(action, RepeatLaunchOptions.FromIndex(RepeatLaunchOptions.ToIndex(action)));
        }
    }

    [TestMethod]
    public void UnknownValueOrOutOfRangeIndex_FallsBackToOpeningMainWindow()
    {
        var undefined = (WebOpenAction)99;

        Assert.AreEqual(
            RepeatLaunchOptions.ToIndex(WebOpenAction.MainWindow),
            RepeatLaunchOptions.ToIndex(undefined));
        Assert.AreEqual(WebOpenAction.MainWindow, RepeatLaunchOptions.FromIndex(-1));
        Assert.AreEqual(WebOpenAction.MainWindow, RepeatLaunchOptions.FromIndex(4));
        Assert.AreEqual(WebOpenAction.MainWindow, RepeatLaunchOptions.DefaultAction);
    }
}
