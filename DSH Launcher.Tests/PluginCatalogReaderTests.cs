using System.Linq;
using System.Text.Json;
using DSH_Launcher.Models;
using DSH_Launcher.Services;
using DSH_Launcher.Services.Plugins;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DSH_Launcher.Tests;

/// <summary>
/// 两份社区插件目录的解析归一。
/// 两份目录的字段体系完全不同(完整单词 vs 缩写),写错的表现是"列表整片空白"或"字段错位",
/// 而不是异常 —— 只能靠固定样本来锁。
/// </summary>
[TestClass]
public sealed class PluginCatalogReaderTests
{
    private const string AwesomeDshPluginJson = """
    {
      "updated": "2026-01-02",
      "categories": { "ui": { "zh": "界面", "en": "UI" } },
      "plugins": [
        {
          "name": "p1",
          "owner": "alice",
          "url": "https://github.com/alice/p1",
          "page": "https://awesome-dsh-plugin.com/plugins/p1",
          "category": "ui",
          "description": { "zh": "中文说明", "en": "English text" },
          "npm": "@scope/p1",
          "version": "1.2.3",
          "stars": 1234,
          "downloads": 5000,
          "added": "2026-01-01",
          "install": "dsh plugin --profile web add @scope/p1",
          "tarball": "https://github.com/alice/p1/releases/download/v1.2.3/p1.tgz"
        },
        {
          "name": "p2",
          "owner": "carol",
          "category": "unknown-category",
          "stars": 10
        }
      ]
    }
    """;

    private const string DshPluginOrgJson = """
    [
      {
        "n": "plugin2",
        "o": "bob",
        "s": "slug2",
        "c": "dev",
        "d": "中文描述",
        "r": { "repo": "bob/plugin2", "npmPackage": "plugin2" },
        "ic": "dsh plugin add plugin2",
        "igc": "dsh plugin add github:bob/plugin2",
        "v": "verified",
        "sg": 42,
        "fk": 7,
        "a": "2026-02-03",
        "vr": "v0.9.0"
      }
    ]
    """;

    [TestMethod]
    public void Parse_AwesomeDshPluginShape_MapsFullWordFields()
    {
        var catalog = Parse(AwesomeDshPluginJson, "https://awesome-dsh-plugin.com/plugins.json");

        Assert.AreEqual("2026-01-02", catalog.Updated);
        StringAssert.Contains(catalog.SourceUrl, "awesome-dsh-plugin");
        Assert.AreEqual(2, catalog.Entries.Count);

        var entry = catalog.Entries[0];
        Assert.AreEqual("p1", entry.Name);
        Assert.AreEqual("alice", entry.Owner);
        Assert.AreEqual("https://github.com/alice/p1", entry.RepoUrl);
        Assert.AreEqual("https://awesome-dsh-plugin.com/plugins/p1", entry.PageUrl);
        Assert.AreEqual("ui", entry.Category);
        Assert.AreEqual("界面", entry.CategoryText);
        Assert.AreEqual("中文说明", entry.Description);
        Assert.AreEqual("@scope/p1", entry.Npm);
        Assert.AreEqual("1.2.3", entry.Version);
        Assert.AreEqual(1234, entry.Stars);
        Assert.AreEqual(5000, entry.Downloads);
        Assert.IsNull(entry.Forks);
        Assert.IsFalse(entry.Verified);
        Assert.AreEqual("@scope/p1", entry.InstallSpec);
        StringAssert.Contains(entry.FallbackInstallSpec, "p1.tgz");
        Assert.AreEqual("npm", entry.SourceText);
        Assert.AreEqual("v1.2.3", entry.VersionText);
    }

    [TestMethod]
    public void Parse_AwesomeDshPluginShape_UnknownCategoryFallsBackToId()
    {
        var catalog = Parse(AwesomeDshPluginJson, "src");

        Assert.AreEqual("unknown-category", catalog.Entries[1].CategoryText);
        Assert.AreEqual(0, catalog.Entries[1].Downloads ?? 0);
    }

    [TestMethod]
    public void Parse_DshPluginOrgShape_MapsAbbreviatedFields()
    {
        var catalog = Parse(DshPluginOrgJson, "https://api.dsh-plugin.org/plugins.zh.json");

        // 该目录顶层没有 updated 字段 —— 不编造,置空让界面退化成只显示拉取时间
        Assert.AreEqual(string.Empty, catalog.Updated);
        Assert.AreEqual(1, catalog.Entries.Count);

        var entry = catalog.Entries[0];
        Assert.AreEqual("plugin2", entry.Name);
        Assert.AreEqual("bob", entry.Owner);
        Assert.AreEqual("https://github.com/bob/plugin2", entry.RepoUrl);
        Assert.AreEqual("https://dsh-plugin.org/plugins/bob/slug2", entry.PageUrl);
        Assert.AreEqual("开发与运维", entry.CategoryText);
        Assert.AreEqual("中文描述", entry.DescriptionZh);
        Assert.AreEqual("0.9.0", entry.Version);   // vr 的 v 前缀被去掉
        Assert.AreEqual(42, entry.Stars);
        Assert.IsNull(entry.Downloads);            // 该目录不提供下载量
        Assert.AreEqual(7, entry.Forks);
        Assert.IsTrue(entry.Verified);
        Assert.AreEqual("plugin2", entry.InstallSpec);
        // 备选规格同样是"从 dsh 命令行里取出 spec",不是原样保留整条命令
        Assert.AreEqual("github:bob/plugin2", entry.FallbackInstallSpec);
        StringAssert.Contains(entry.MetaText, "★ 42");
        StringAssert.Contains(entry.MetaText, "Fork 7");
        StringAssert.Contains(entry.MetaText, "收录 2026-02-03");
    }

    [TestMethod]
    public void Parse_EmptyCatalog_DoesNotThrow()
    {
        Assert.AreEqual(0, Parse("[]", "src").Entries.Count);
        Assert.AreEqual(0, Parse("{}", "src").Entries.Count);
        Assert.AreEqual(0, Parse("""{ "plugins": [] }""", "src").Entries.Count);
    }

    [TestMethod]
    public void EffectiveInstallSpec_FallsBackThroughNpmThenGitHub()
    {
        Assert.AreEqual("@a/b", new PluginCatalogEntry { InstallSpec = "@a/b" }.EffectiveInstallSpec);
        Assert.AreEqual("pkg", new PluginCatalogEntry { Npm = "pkg" }.EffectiveInstallSpec);
        Assert.AreEqual("github:owner/repo", new PluginCatalogEntry { Owner = "owner", Name = "repo" }.EffectiveInstallSpec);
        Assert.AreEqual(string.Empty, new PluginCatalogEntry().EffectiveInstallSpec);
    }

    [DataTestMethod]
    [DataRow("dsh plugin --profile web add @scope/name", "@scope/name")]
    [DataRow("dsh plugin --profile web add github:owner/repo", "github:owner/repo")]
    [DataRow("dsh plugin add /local/path", "/local/path")]
    [DataRow("", "")]
    public void ExtractInstallSpec_PullsSpecOutOfTheDshCommandLine(string install, string expected)
    {
        Assert.AreEqual(expected, PluginCatalogReader.ExtractInstallSpec(install));
    }

    [TestMethod]
    public void ExtractInstallSpec_LineWithoutAdd_ReturnsTrimmedInputVerbatim()
    {
        // 认不出来时原样交给调用方,由 pnpm 给出真正的错误信息(而不是悄悄变成空规格)
        Assert.AreEqual("pnpm install foo", PluginCatalogReader.ExtractInstallSpec("  pnpm install foo  "));
    }

    private static PluginCatalog Parse(string json, string sourceUrl)
    {
        using var document = JsonDocument.Parse(json);
        return PluginCatalogReader.Parse(document.RootElement, sourceUrl);
    }
}
