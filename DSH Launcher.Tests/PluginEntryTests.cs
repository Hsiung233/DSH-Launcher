using System.Collections.Generic;
using System.ComponentModel;
using DSH_Launcher.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DSH_Launcher.Tests;

/// <summary>
/// <see cref="PluginEntry"/> 的界面状态。
/// 这些属性存在的唯一理由是**列表是虚拟化的**:容器的状态会被回收复用,
/// 所以"勾选""待确认卸载"这类状态必须挂在数据对象上(踩过的坑见属性注释)。
/// </summary>
[TestClass]
public sealed class PluginEntryTests
{
    [TestMethod]
    public void UninstallText_FollowsPendingState()
    {
        var entry = new PluginEntry { Name = "pkg" };

        Assert.AreEqual("卸载", entry.UninstallText);
        entry.IsPendingUninstall = true;
        Assert.AreEqual("确认卸载", entry.UninstallText);
        entry.IsPendingUninstall = false;
        Assert.AreEqual("卸载", entry.UninstallText);
    }

    [TestMethod]
    public void IsPendingUninstall_RaisesChangeForBothStateAndText()
    {
        var entry = new PluginEntry { Name = "pkg" };
        var changed = new List<string?>();
        entry.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        entry.IsPendingUninstall = true;

        CollectionAssert.Contains(changed, nameof(PluginEntry.IsPendingUninstall));
        CollectionAssert.Contains(
            changed,
            nameof(PluginEntry.UninstallText)); // 只通知前者会让按钮文字不变
    }

    [TestMethod]
    public void IsPendingUninstall_SettingSameValue_DoesNotNotify()
    {
        var entry = new PluginEntry { Name = "pkg" };
        var raised = 0;
        entry.PropertyChanged += (_, _) => raised++;

        entry.IsPendingUninstall = false; // 本来就是 false

        Assert.AreEqual(0, raised);
    }

    [TestMethod]
    public void IsSelected_StillNotifiesOnlyItself()
    {
        var entry = new PluginEntry { Name = "pkg" };
        var changed = new List<string?>();
        entry.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        entry.IsSelected = true;

        CollectionAssert.AreEqual(new[] { nameof(PluginEntry.IsSelected) }, changed);
    }

    [TestMethod]
    public void CanToggleAndCanUninstall_ReflectOriginOfEntry()
    {
        // 启停只对用户安装的插件开放(dsh 自带条目归 dsh 预设管理);
        // 只有 dependencies 里的包可卸载
        var builtIn = new PluginEntry { Id = "ui-schedule", Name = "@dsh/ui-schedule", InComposition = true, IsBundle = true, IsInstalled = false };
        Assert.IsFalse(builtIn.CanToggle); // 自带层:只读展示,不由启动器启停
        Assert.IsFalse(builtIn.CanUninstall);
        Assert.AreEqual("自带层", builtIn.OriginText);
        Assert.IsTrue(builtIn.IsBuiltInBundle);

        var installed = new PluginEntry { Id = "mine", Name = "mine", InComposition = true, IsInstalled = true, IsBundle = true };
        Assert.IsTrue(installed.CanToggle);
        Assert.IsTrue(installed.CanUninstall);
        Assert.AreEqual("已安装 · 层", installed.OriginText);
        Assert.IsFalse(installed.IsBuiltInBundle);

        var notComposed = new PluginEntry { Name = "plain-dep", IsInstalled = true, InComposition = false };
        Assert.IsFalse(notComposed.CanToggle);
        Assert.IsTrue(notComposed.CanUninstall);
        Assert.AreEqual("未参与组合", notComposed.StateText);

        // 内部组合条目(既非 bundle 也非依赖):同样只读,归 dsh 预设管理
        var internalEntry = new PluginEntry { Id = "core-x", Name = "core-x", InComposition = true };
        Assert.IsFalse(internalEntry.CanToggle);
        Assert.IsFalse(internalEntry.CanUninstall);
        Assert.AreEqual("组合条目", internalEntry.OriginText);
    }
}
