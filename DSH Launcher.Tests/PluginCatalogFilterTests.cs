using System.Collections.Generic;
using System.Linq;
using DSH_Launcher.Models;
using DSH_Launcher.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DSH_Launcher.Tests;

/// <summary>
/// 目录条目的筛选与排序(纯内存,一万多条)。
/// 面板上"搜不到"或"排序看起来没生效"都出在这里,而它是无副作用的纯函数,最适合锁住。
/// </summary>
[TestClass]
public sealed class PluginCatalogFilterTests
{
    private static readonly PluginCatalog Catalog = new()
    {
        Entries =
        [
            new PluginCatalogEntry { Name = "alpha", Owner = "zoe", Category = "ui", Stars = 10, Downloads = 100, Added = "2026-01-01", DescriptionZh = "聊天增强" },
            new PluginCatalogEntry { Name = "beta", Owner = "amy", Category = "dev", Stars = 50, Downloads = 5, Added = "2026-03-01", DescriptionEn = "Chat helper" },
            new PluginCatalogEntry { Name = "gamma", Owner = "bob", Category = "ui", Stars = 50, Downloads = 900, Added = "2026-02-01", DescriptionZh = "无关描述" },
            new PluginCatalogEntry { Name = "delta", Owner = "carol", Category = "dev", Stars = 1, Downloads = null, Added = "2025-12-01", DescriptionZh = "聊天记录导出" },
        ],
    };

    [TestMethod]
    public void FilterCatalog_EmptyFilters_KeepsEverything()
    {
        var result = PluginService.FilterCatalog(Catalog, string.Empty, string.Empty, PluginCatalogSort.Stars);

        Assert.AreEqual(4, result.Count);
    }

    [TestMethod]
    public void FilterCatalog_ByCategory_MatchesExactly()
    {
        var result = PluginService.FilterCatalog(Catalog, string.Empty, "dev", PluginCatalogSort.Stars);

        CollectionAssert.AreEquivalent(new[] { "beta", "delta" }, result.Select(e => e.Name).ToArray());
    }

    [TestMethod]
    public void FilterCatalog_KeywordSearchesNameOwnerAndBothDescriptions()
    {
        // 关键词同时命中包名(alpha/beta?)、作者(amy)、中文描述与英文描述
        var byName = PluginService.FilterCatalog(Catalog, "gamma", string.Empty, PluginCatalogSort.Stars);
        Assert.AreEqual(1, byName.Count);

        var byOwner = PluginService.FilterCatalog(Catalog, "AMY", string.Empty, PluginCatalogSort.Stars);
        Assert.AreEqual("beta", byOwner.Single().Name);

        var byChineseDescription = PluginService.FilterCatalog(Catalog, "聊天", string.Empty, PluginCatalogSort.Stars);
        CollectionAssert.AreEquivalent(new[] { "alpha", "delta" }, byChineseDescription.Select(e => e.Name).ToArray());

        var byEnglishDescription = PluginService.FilterCatalog(Catalog, "helper", string.Empty, PluginCatalogSort.Stars);
        Assert.AreEqual("beta", byEnglishDescription.Single().Name);
    }

    [TestMethod]
    public void FilterCatalog_TrimsKeyword()
    {
        Assert.AreEqual(1, PluginService.FilterCatalog(Catalog, "  gamma  ", string.Empty, PluginCatalogSort.Stars).Count);
    }

    [TestMethod]
    public void FilterCatalog_SortByStars_TieBreaksOnDownloadsThenName()
    {
        var result = PluginService.FilterCatalog(Catalog, string.Empty, string.Empty, PluginCatalogSort.Stars);

        // gamma 与 beta 都是 50 星,gamma 下载量更高 ⇒ 排前面
        Assert.AreEqual("gamma", result[0].Name);
        Assert.AreEqual("beta", result[1].Name);
        Assert.AreEqual("alpha", result[2].Name);
        Assert.AreEqual("delta", result[3].Name);
    }

    [TestMethod]
    public void FilterCatalog_SortByDownloads_TreatsMissingDownloadsAsZero()
    {
        var result = PluginService.FilterCatalog(Catalog, string.Empty, string.Empty, PluginCatalogSort.Downloads);

        Assert.AreEqual("gamma", result[0].Name);   // 900
        Assert.AreEqual("alpha", result[1].Name);   // 100
        Assert.AreEqual("beta", result[2].Name);    // 5
        Assert.AreEqual("delta", result[3].Name);   // null → 0
    }

    [TestMethod]
    public void FilterCatalog_SortByNewest_UsesAddedDateDescending()
    {
        var result = PluginService.FilterCatalog(Catalog, string.Empty, string.Empty, PluginCatalogSort.Newest);

        CollectionAssert.AreEqual(
            new[] { "beta", "gamma", "alpha", "delta" },
            result.Select(e => e.Name).ToArray());
    }
}
