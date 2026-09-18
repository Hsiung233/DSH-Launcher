using System;
using System.Collections.Generic;
using System.IO;
using DSH_Launcher.Services;
using DSH_Launcher.Services.Plugins;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DSH_Launcher.Tests;

/// <summary>
/// <c>cordis.patch.yml</c> 托管区块的读写。
/// 这里全是**文件格式知识**,而且踩过坑(空数组 <c>[]</c> 本身就是一份完整 YAML 文档,
/// 在它后面追加条目会让 dsh 直接解析失败)。任何一次"顺手改成追加"都会让用户的插件页失效。
/// 测试只碰临时目录,不碰真实的 profile。
/// </summary>
[TestClass]
public sealed class PluginPatchFileTests
{
    private string _dir = string.Empty;

    private string PatchPath => Path.Combine(this._dir, "cordis.patch.yml");

    [TestInitialize]
    public void CreateTempDirectory()
    {
        this._dir = Path.Combine(Path.GetTempPath(), "dsh-launcher-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(this._dir);
    }

    [TestCleanup]
    public void DeleteTempDirectory()
    {
        try
        {
            Directory.Delete(this._dir, recursive: true);
        }
        catch (Exception)
        {
            // 清理失败不影响测试结论
        }
    }

    [TestMethod]
    public void Read_MissingFile_ReturnsEmpty()
    {
        Assert.AreEqual(0, PluginPatchFile.Read(this.PatchPath).Count);
    }

    [TestMethod]
    public void WriteThenRead_RoundTripsOverrides()
    {
        var overrides = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["plugin-b"] = true,
            ["plugin-a"] = false,
        };

        PluginPatchFile.Write(this.PatchPath, overrides);
        var read = PluginPatchFile.Read(this.PatchPath);

        Assert.AreEqual(2, read.Count);
        Assert.IsFalse(read["plugin-a"]);
        Assert.IsTrue(read["plugin-b"]);

        // 托管区块必须带注释边界:用户一眼能看出这块是启动器写的
        var text = File.ReadAllText(this.PatchPath);
        StringAssert.Contains(text, PluginPatchFile.ManagedBegin);
        StringAssert.Contains(text, PluginPatchFile.ManagedEnd);
    }

    [TestMethod]
    public void Write_SortsIdsForStableDiff()
    {
        PluginPatchFile.Write(this.PatchPath, new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["zzz"] = true,
            ["aaa"] = true,
        });

        var text = File.ReadAllText(this.PatchPath);
        Assert.IsTrue(
            text.IndexOf("id: aaa", StringComparison.Ordinal) < text.IndexOf("id: zzz", StringComparison.Ordinal),
            "覆盖条目应按 id 排序写出,否则每次保存都会产生无意义 diff");
    }

    [TestMethod]
    public void Write_IsIdempotent()
    {
        var overrides = new Dictionary<string, bool>(StringComparer.Ordinal) { ["p"] = true };

        PluginPatchFile.Write(this.PatchPath, overrides);
        var first = File.ReadAllText(this.PatchPath);
        PluginPatchFile.Write(this.PatchPath, overrides);
        var second = File.ReadAllText(this.PatchPath);

        Assert.AreEqual(first, second, "重复写入不应累积托管区块或空行");
    }

    [TestMethod]
    public void Write_KeepsUserEntriesOutsideManagedBlock()
    {
        File.WriteAllText(this.PatchPath, "- id: user-own\n  disabled: true\n");

        PluginPatchFile.Write(this.PatchPath, new Dictionary<string, bool>(StringComparer.Ordinal) { ["ours"] = true });

        var text = File.ReadAllText(this.PatchPath);
        StringAssert.Contains(text, "id: user-own");

        // 只读托管区块:用户自己的条目不参与启停覆盖表(它们本来就在 dsh 侧生效)
        var read = PluginPatchFile.Read(this.PatchPath);
        Assert.AreEqual(1, read.Count);
        Assert.IsTrue(read.ContainsKey("ours"));
    }

    [TestMethod]
    public void Write_RemovesEmptyArrayMarkerWhenOverridesExist()
    {
        // [] 是一份完整的 YAML 文档:不删掉它,后面追加的条目会让 dsh 解析失败
        File.WriteAllText(this.PatchPath, "[]\n");

        PluginPatchFile.Write(this.PatchPath, new Dictionary<string, bool>(StringComparer.Ordinal) { ["p"] = true });

        var text = File.ReadAllText(this.PatchPath);
        Assert.IsFalse(
            Array.Exists(text.Split('\n'), line => line.Trim() == "[]"),
            $"有覆盖时必须删掉空数组标记,实际内容:\n{text}");
        Assert.IsTrue(PluginPatchFile.Read(this.PatchPath).ContainsKey("p"));
    }

    [TestMethod]
    public void Write_RestoresEmptyArrayMarkerWhenNoOverridesLeft()
    {
        File.WriteAllText(this.PatchPath, "[]\n");
        PluginPatchFile.Write(this.PatchPath, new Dictionary<string, bool>(StringComparer.Ordinal) { ["p"] = true });

        // 用户把最后一条覆盖"恢复默认"→ 应当回到干净的空数组文档
        PluginPatchFile.Write(this.PatchPath, new Dictionary<string, bool>(StringComparer.Ordinal));

        var text = File.ReadAllText(this.PatchPath);
        Assert.IsFalse(text.Contains(PluginPatchFile.ManagedBegin, StringComparison.Ordinal), "托管区块应被摘除");
        Assert.AreEqual("[]", text.Trim());
        Assert.AreEqual(0, PluginPatchFile.Read(this.PatchPath).Count);
    }

    [TestMethod]
    public void Write_PreservesLfLineEndings()
    {
        File.WriteAllText(this.PatchPath, "- id: user-own\n  disabled: false\n");

        PluginPatchFile.Write(this.PatchPath, new Dictionary<string, bool>(StringComparer.Ordinal) { ["p"] = true });

        var text = File.ReadAllText(this.PatchPath);
        Assert.IsFalse(text.Contains("\r\n", StringComparison.Ordinal), "原来是 LF 的文件不该被整体改成 CRLF");
    }

    [TestMethod]
    public void Write_PreservesCrlfLineEndings()
    {
        File.WriteAllText(this.PatchPath, "- id: user-own\r\n  disabled: false\r\n");

        PluginPatchFile.Write(this.PatchPath, new Dictionary<string, bool>(StringComparer.Ordinal) { ["p"] = true });

        StringAssert.Contains(File.ReadAllText(this.PatchPath), "\r\n");
    }

    [TestMethod]
    public void Read_IgnoresEntriesOutsideManagedBlock()
    {
        File.WriteAllText(
            this.PatchPath,
            "- id: user-own\n"
            + "  disabled: true\n"
            + PluginPatchFile.ManagedBegin + "\n"
            + "- id: ours\n"
            + "  disabled: false\n"
            + PluginPatchFile.ManagedEnd + "\n");

        var read = PluginPatchFile.Read(this.PatchPath);

        Assert.AreEqual(1, read.Count);
        Assert.IsFalse(read["ours"]);
    }
}
