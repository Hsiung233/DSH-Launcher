using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace DSH_Launcher.Services
{
    /// <summary>
    /// “环境设置”(npm 源 / 代理)的唯一落地点:把用户在设置页选的值翻译成
    /// **子进程环境变量**与 **HTTP 客户端代理**,其它地方不要各自拼一套。
    ///
    /// 作用范围(有意为之):
    /// <list type="bullet">
    /// <item>走 <see cref="DshService.StartHidden"/> 的全部子进程 —— npm 的
    /// <c>ls/view/install</c>、<c>where dsh</c> 探测、<c>dsh plugin ...</c>(pnpm 安装/卸载)。</item>
    /// <item><see cref="PluginService"/> 拉取插件目录用的 HTTP 客户端。</item>
    /// <item>**不包括** <c>dsh web</c> 服务进程:它是本机服务,注入代理反而会让
    /// WebView/浏览器访问 <c>127.0.0.1</c> 走代理而变慢或失败。</item>
    /// </list>
    ///
    /// 为什么用环境变量而不是命令行参数:<c>npm</c> 有 <c>--registry</c>,但
    /// <c>dsh plugin</c> 只是把参数转发给 profile 目录下的 <c>pnpm</c>,拼参数容易漏;
    /// 而 <c>npm_config_*</c> 是 npm/pnpm 共同认的配置通道(等价于 .npmrc 里的同名键)。
    /// </summary>
    public static class ChildEnvironment
    {
        /// <summary>“使用配置源”时本机 .npmrc 的默认地址(仅在界面/日志里做提示用)。</summary>
        public const string PublicNpmRegistry = "https://registry.npmjs.org";

        /// <summary>已知镜像的地址表。<see cref="NpmRegistrySource.Config"/> 不在表内 = 不注入。</summary>
        private static readonly Dictionary<NpmRegistrySource, string> RegistryUrls = new()
        {
            [NpmRegistrySource.NpmOfficial] = PublicNpmRegistry,
            [NpmRegistrySource.Npmmirror] = "https://registry.npmmirror.com",
            [NpmRegistrySource.TencentCloud] = "https://mirrors.cloud.tencent.com/npm/",
            [NpmRegistrySource.HuaweiCloud] = "https://repo.huaweicloud.com/repository/npm/",
        };

        /// <summary>代理相关的环境变量名。</summary>
        /// <remarks>
        /// 两组都要设:
        /// ① <c>HTTP_PROXY/HTTPS_PROXY</c>(含小写形式)—— 通用约定,pnpm/undici/curl 等都认;
        /// ② <c>npm_config_proxy/npm_config_https_proxy</c> —— **npm 自身不读 HTTP_PROXY**,
        /// 它只认 proxy/https-proxy 配置项(可由 <c>npm_config_*</c> 环境变量提供)。
        /// 只设一组会出现“pnpm 走了代理、npm 没走”这种半生效状态。
        /// </remarks>
        private static readonly string[] ProxyVariableNames =
        [
            "HTTP_PROXY", "HTTPS_PROXY", "http_proxy", "https_proxy",
            "npm_config_proxy", "npm_config_https_proxy",
        ];

        /// <summary><c>NO_PROXY</c> 一侧的变量名(npm 的配置项叫 <c>noproxy</c>)。</summary>
        private static readonly string[] NoProxyVariableNames =
        [
            "NO_PROXY", "no_proxy", "npm_config_noproxy",
        ];

        /// <summary>取枚举对应的 registry 地址;“使用配置源”返回 null(= 不注入)。</summary>
        public static string? ResolveRegistry(NpmRegistrySource source)
            => RegistryUrls.TryGetValue(source, out var url) ? url : null;

        /// <summary>枚举对应的人员可读名称(用于界面提示与日志)。</summary>
        public static string RegistryDisplayName(NpmRegistrySource source) => source switch
        {
            NpmRegistrySource.NpmOfficial => "npm 官方",
            NpmRegistrySource.Npmmirror => "npmmirror(淘宝)",
            NpmRegistrySource.TencentCloud => "腾讯云",
            NpmRegistrySource.HuaweiCloud => "华为云",
            _ => "使用配置源",
        };

        /// <summary>当前 npm 源的一句话描述(写日志用)。</summary>
        public static string DescribeRegistry()
        {
            var source = SettingsService.Instance.Settings.NpmRegistry;
            var url = ResolveRegistry(source);
            return url is null
                ? "使用配置源(沿用本机 .npmrc / 环境变量)"
                : $"{RegistryDisplayName(source)} {url}";
        }

        /// <summary>当前代理的一句话描述(写日志用)。</summary>
        public static string DescribeProxy()
        {
            var proxy = NormalizeProxyUrl(SettingsService.Instance.Settings.ProxyUrl);
            if (proxy.Length == 0)
            {
                return "未使用";
            }

            var noProxy = NormalizeNoProxy(SettingsService.Instance.Settings.NoProxy);
            return noProxy.Length == 0 ? proxy : $"{proxy}(不走代理: {noProxy})";
        }

        /// <summary>
        /// 归一化代理地址:全角冒号/斜杠转半角(中文输入法下极容易打出来)、去空白;
        /// 缺协议时补 <c>http://</c>(用户常只填 <c>127.0.0.1:7890</c>);去掉末尾斜杠。空串 = 不使用代理。
        /// </summary>
        public static string NormalizeProxyUrl(string? raw)
        {
            var value = (raw?.Trim() ?? string.Empty)
                .Replace('：', ':')
                .Replace('／', '/');

            if (value.Length == 0)
            {
                return string.Empty;
            }

            if (!value.Contains("://", StringComparison.Ordinal))
            {
                value = "http://" + value;
            }

            return value.TrimEnd('/');
        }

        /// <summary>
        /// 归一化“不走代理”列表:按逗号/分号切分、去空白与空项,再用逗号重新拼接
        /// (统一成一种写法,免得看起来“没变”但确实重新保存了一次)。
        /// </summary>
        public static string NormalizeNoProxy(string? raw)
            => string.Join(",", (raw ?? string.Empty)
                .Replace('，', ',')
                .Split([',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Trim())
                .Where(part => part.Length > 0));

        /// <summary>
        /// 代理配置的指纹(用于判断已建好的 <see cref="HttpClient"/> 是否需要重建)。
        /// 代理改了免重启即可生效:拉目录前比对一次,不同就换掉旧客户端。
        /// </summary>
        public static string ProxyKey()
        {
            var settings = SettingsService.Instance.Settings;
            return NormalizeProxyUrl(settings.ProxyUrl) + "\n" + NormalizeNoProxy(settings.NoProxy);
        }

        /// <summary>把当前环境设置注入子进程(registry + 代理)。</summary>
        public static void Apply(ProcessStartInfo psi)
        {
            var settings = SettingsService.Instance.Settings;

            var registry = ResolveRegistry(settings.NpmRegistry);
            if (registry is not null)
            {
                psi.Environment["npm_config_registry"] = registry;
            }

            var proxy = NormalizeProxyUrl(settings.ProxyUrl);
            if (proxy.Length == 0)
            {
                return;
            }

            foreach (var name in ProxyVariableNames)
            {
                psi.Environment[name] = proxy;
            }

            var noProxy = NormalizeNoProxy(settings.NoProxy);
            if (noProxy.Length > 0)
            {
                foreach (var name in NoProxyVariableNames)
                {
                    psi.Environment[name] = noProxy;
                }
            }
        }

        /// <summary>
        /// 给应用自己发起的 HTTP 请求套上代理(插件目录下载)。
        /// 子进程的环境变量对进程内的 <see cref="HttpClient"/> 无效,必须显式设 handler。
        /// </summary>
        public static void ApplyProxy(HttpClientHandler handler)
        {
            var proxy = NormalizeProxyUrl(SettingsService.Instance.Settings.ProxyUrl);
            if (proxy.Length == 0)
            {
                return;
            }

            var webProxy = new WebProxy(proxy)
            {
                // 本机地址不经代理:目录里没有本机地址,但显式声明语义更清楚
                BypassProxyOnLocal = true,
            };

            foreach (var entry in NormalizeNoProxy(SettingsService.Instance.Settings.NoProxy)
                .Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                // 条目既支持精确主机名也支持域名后缀(localhost / .corp.com),统一按“后缀匹配”处理:
                // .NET 的 BypassList 是正则表,主机名要转义,只匹配整个主机(含子域)以免误伤同前缀域名
                webProxy.BypassList = [.. (webProxy.BypassList ?? []), $@"(^|\.){Regex.Escape(entry.TrimStart('*', '.'))}$"];
            }

            handler.Proxy = webProxy;
            handler.UseProxy = true;
        }
    }
}
