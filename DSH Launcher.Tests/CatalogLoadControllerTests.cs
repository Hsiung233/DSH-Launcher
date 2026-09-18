using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DSH_Launcher.Models;
using DSH_Launcher.Views.Shared;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DSH_Launcher.Tests;

/// <summary>
/// 插件目录加载状态机:缓存命中、同地址去重、世代号。
/// <para>
/// 目录拉取要 0.1-8 秒,用户完全可能在这个窗口里再切来源 —— "慢的旧来源最后落地"会让列表与下拉框
/// 显示成两份不同的数据。这类时序问题靠点界面几乎复现不出来,这里用受控的 <see cref="TaskCompletionSource"/>
/// 把每种时序固定下来。
/// </para>
/// </summary>
[TestClass]
public sealed class CatalogLoadControllerTests
{
    private const string UrlA = "https://a.example/plugins.json";
    private const string UrlB = "https://b.example/plugins.json";

    private string _currentUrl = UrlA;
    private readonly Dictionary<string, PluginCatalog> _cache = new(StringComparer.Ordinal);
    private readonly List<string> _loadCalls = [];
    private Func<bool, Task<PluginCatalog>> _load = _ => Task.FromResult(Catalog(UrlA));

    private CatalogLoadController CreateController() => new(
        () => this._currentUrl,
        url => this._cache.TryGetValue(url, out var cached) ? cached : null,
        forceReload =>
        {
            this._loadCalls.Add(this._currentUrl + (forceReload ? ":force" : string.Empty));
            return this._load(forceReload);
        });

    private static PluginCatalog Catalog(string sourceUrl) => new() { SourceUrl = sourceUrl };

    private static TaskCompletionSource<PluginCatalog> Pending() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    [TestMethod]
    public async Task EnsureAsync_CacheHit_DisplaysWithoutLoading()
    {
        this._cache[UrlA] = Catalog(UrlA);
        var controller = this.CreateController();

        var result = await controller.EnsureAsync();

        Assert.AreEqual(CatalogLoadOutcome.DisplayedFromCache, result.Outcome);
        Assert.AreSame(this._cache[UrlA], controller.Catalog);
        Assert.AreEqual(0, this._loadCalls.Count, "命中缓存不该发起拉取");
        Assert.IsFalse(controller.IsLoading);
    }

    [TestMethod]
    public async Task EnsureAsync_SameUrlAlreadyLoading_DoesNotStartSecondLoad()
    {
        var gate = Pending();
        this._load = _ => gate.Task;
        var controller = this.CreateController();

        var first = controller.EnsureAsync();
        var second = await controller.EnsureAsync();

        Assert.AreEqual(CatalogLoadOutcome.AlreadyLoading, second.Outcome);
        Assert.AreEqual(1, this._loadCalls.Count, "同一地址不该重复发起");
        Assert.IsTrue(controller.IsLoading);

        gate.SetResult(Catalog(UrlA));
        Assert.AreEqual(CatalogLoadOutcome.Loaded, (await first).Outcome);
        Assert.IsFalse(controller.IsLoading, "拉取结束后忙碌态必须复位");
    }

    [TestMethod]
    public async Task EnsureAsync_ForceReload_BypassesCacheAndRepeatsLoads()
    {
        this._cache[UrlA] = Catalog(UrlA);
        this._load = _ => Task.FromResult(Catalog(UrlA));
        var controller = this.CreateController();

        var result = await controller.EnsureAsync(forceReload: true);
        await controller.EnsureAsync(forceReload: true);

        Assert.AreEqual(CatalogLoadOutcome.Loaded, result.Outcome);
        CollectionAssert.AreEqual(
            new[] { UrlA + ":force", UrlA + ":force" },
            this._loadCalls,
            "「刷新目录」必须绕过缓存,连续点两次就是拉两次");
    }

    [TestMethod]
    public async Task EnsureAsync_SlowOldSource_MustNotOverwriteNewerResult()
    {
        var gateA = Pending();
        var gateB = Pending();
        this._load = _ => this._currentUrl == UrlA ? gateA.Task : gateB.Task;
        var controller = this.CreateController();

        var loadA = controller.EnsureAsync();       // 开始拉 A(慢)
        this._currentUrl = UrlB;                    // 用户切到 B
        var loadB = controller.EnsureAsync();       // 开始拉 B(快)
        Assert.AreEqual(2, this._loadCalls.Count);

        gateA.SetResult(Catalog(UrlA));             // A 后完成
        gateB.SetResult(Catalog(UrlB));

        Assert.AreEqual(CatalogLoadOutcome.Superseded, (await loadA).Outcome);
        Assert.AreEqual(CatalogLoadOutcome.Loaded, (await loadB).Outcome);
        Assert.AreEqual(UrlB, controller.Catalog!.SourceUrl, "列表里必须是最后选中的那份目录");
    }

    [TestMethod]
    public async Task EnsureAsync_CacheHitSupersedesInFlightRequest()
    {
        var gateA = Pending();
        this._load = _ => gateA.Task;
        this._cache[UrlB] = Catalog(UrlB);
        var controller = this.CreateController();

        var loadA = controller.EnsureAsync();                 // A 在飞
        this._currentUrl = UrlB;
        var cachedResult = await controller.EnsureAsync();    // B 命中缓存,立即显示

        Assert.AreEqual(CatalogLoadOutcome.DisplayedFromCache, cachedResult.Outcome);
        Assert.AreEqual(UrlB, controller.Catalog!.SourceUrl);
        Assert.IsFalse(controller.IsLoading, "命中缓存是瞬时的,不该继续显示忙碌");

        // A 最后才回来:必须被丢弃,否则界面会显示成 A 而下拉是 B
        gateA.SetResult(Catalog(UrlA));
        Assert.AreEqual(CatalogLoadOutcome.Superseded, (await loadA).Outcome);
        Assert.AreEqual(UrlB, controller.Catalog!.SourceUrl);
    }

    [TestMethod]
    public async Task EnsureAsync_Failure_ReportsErrorAndClearsLoading()
    {
        this._load = _ => Task.FromException<PluginCatalog>(new InvalidOperationException("boom"));
        var controller = this.CreateController();

        var result = await controller.EnsureAsync();

        Assert.AreEqual(CatalogLoadOutcome.Failed, result.Outcome);
        Assert.AreEqual("boom", result.Error);
        Assert.IsNull(controller.Catalog, "失败时不回退旧数据(上游刻意不做陈旧缓存)");
        Assert.IsFalse(controller.IsLoading);
        Assert.AreEqual(UrlA, controller.LastRequestedUrl, "失败提示里要能显示请求的地址");
    }

    [TestMethod]
    public async Task EnsureAsync_FailureOfSupersededRequest_IsDiscarded()
    {
        var gateA = Pending();
        this._load = _ => gateA.Task;
        var controller = this.CreateController();

        var loadA = controller.EnsureAsync();
        this._currentUrl = UrlB;
        this._load = _ => Task.FromResult(Catalog(UrlB));
        var loadB = controller.EnsureAsync();

        gateA.SetException(new InvalidOperationException("boom"));

        Assert.AreEqual(CatalogLoadOutcome.Superseded, (await loadA).Outcome, "被顶替的失败不该弹出错误");
        Assert.AreEqual(CatalogLoadOutcome.Loaded, (await loadB).Outcome);
        Assert.AreEqual(UrlB, controller.Catalog!.SourceUrl);
    }

    [TestMethod]
    public async Task EnsureAsync_OnStartedOnlyFiresWhenLoadReallyStarts()
    {
        this._cache[UrlB] = Catalog(UrlB);
        this._load = _ => Task.FromResult(Catalog(UrlA));
        var controller = this.CreateController();
        var started = 0;

        await controller.EnsureAsync(forceReload: false, onStarted: () => started++);
        Assert.AreEqual(0, started, "命中缓存是瞬时的,不该显示“正在读取…”");

        await controller.EnsureAsync(forceReload: true, onStarted: () => started++);
        Assert.AreEqual(1, started, "真正发起拉取时才回调一次");
    }

    [TestMethod]
    public async Task EnsureAsync_UsesLatestUrlFromResolver()
    {
        this._load = _ => Task.FromResult(Catalog(this._currentUrl));
        var controller = this.CreateController();

        await controller.EnsureAsync();
        this._currentUrl = UrlB;
        await controller.EnsureAsync();

        Assert.AreEqual(UrlB, controller.Catalog!.SourceUrl);
        Assert.AreEqual(UrlB, controller.LastRequestedUrl);
    }
}
