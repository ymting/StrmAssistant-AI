using HarmonyLib;
using StrmAssistant.Options;
using System;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;

namespace StrmAssistant.Mod
{
    /// <summary>
    /// 让 Emby 的出站请求走插件里配置的代理服务器。
    ///
    /// 做法是给 <c>Emby.Server.Implementations.ApplicationHost.CreateHttpClientHandler</c> 挂 Postfix：
    /// Emby 每创建一个 HttpClientHandler，就在这里把 WebProxy 塞进去。
    /// 这样 TMDB/豆瓣等所有走 Emby 内置 HttpClient 的请求都会经过代理，不需要改任何业务代码。
    ///
    /// 注意：不直接引用 Emby.Server.Implementations.dll（那是内部程序集，各版本都可能变），
    /// 而是按类型名反射拿方法，拿不到就降级，不影响插件其它功能。
    /// </summary>
    internal class EnableProxyServer : PatchBase<EnableProxyServer>
    {
        private const string ApplicationHostTypeName = "Emby.Server.Implementations.ApplicationHost";

        private static readonly string[] BypassAddressList =
        {
            "10.*", "172.1[6-9].*", "172.2[0-9].*", "172.3[0-1].*", "192.168.*"
        };

        private static MethodInfo _createHttpClientHandler;

        public EnableProxyServer()
        {
            Initialize();
        }

        protected override void OnInitialize()
        {
            var assembly = Assembly.Load("Emby.Server.Implementations");
            var hostType = assembly?.GetType(ApplicationHostTypeName);

            if (hostType is null)
            {
                Plugin.Instance.Logger.Warn($"{nameof(EnableProxyServer)} - {ApplicationHostTypeName} not found");
                return;
            }

            // 不同版本可能是 CreateHttpClientHandler() 或无参重载，取声明方法里名称匹配、参数最少的那个
            _createHttpClientHandler = AccessTools.Method(hostType, "CreateHttpClientHandler");

            if (_createHttpClientHandler is null)
            {
                Plugin.Instance.Logger.Warn($"{nameof(EnableProxyServer)} - CreateHttpClientHandler not found");
            }
        }

        /// <summary>按当前选项决定 patch 还是 unpatch，由设置保存时与启动时调用。</summary>
        public void Apply()
        {
            var options = GetNetworkOptions();
            if (options?.EnableProxyServer == true && !string.IsNullOrWhiteSpace(options.ProxyServerUrl))
            {
                Patch();
            }
            else
            {
                Unpatch();
            }
        }

        protected override void Prepare(bool apply)
        {
            PatchManager.PatchUnpatch(PatchTracker, apply, _createHttpClientHandler, null,
                nameof(CreateHttpClientHandlerPostfix));
        }

        [HarmonyPostfix]
        private static void CreateHttpClientHandlerPostfix(HttpMessageHandler __result)
        {
            try
            {
                var options = GetNetworkOptions();

                if (options?.EnableProxyServer != true || string.IsNullOrWhiteSpace(options.ProxyServerUrl)) return;
                if (!Uri.TryCreate(options.ProxyServerUrl, UriKind.Absolute, out var proxyUri)) return;

                if (!Common.CommonUtility.TryParseProxyUrl(options.ProxyServerUrl,
                        out _, out _, out _, out var username, out var password))
                {
                    return;
                }

                var proxy = new WebProxy(proxyUri)
                {
                    BypassProxyOnLocal = true,
                    BypassList = BypassAddressList,
                    Credentials = !string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password)
                        ? new NetworkCredential(username, password)
                        : null
                };

                // Emby 4.9 的 CreateHttpClientHandler 返回 HttpMessageHandler，内部实际是 SocketsHttpHandler；
                // SocketsHttpHandler 不在 netstandard2.1 的 API 里，所以统一走反射设置（4.8 的 HttpClientHandler 走强类型）
                if (__result is HttpClientHandler handler)
                {
                    handler.Proxy = proxy;
                    handler.UseProxy = true;

                    if (options.IgnoreCertificateValidation)
                    {
                        handler.ServerCertificateCustomValidationCallback =
                            (request, cert, chain, errors) => true;
                    }
                }
                else
                {
                    ApplyByReflection(__result, proxy, options.IgnoreCertificateValidation);
                }
            }
            catch (Exception e)
            {
                if (Plugin.Instance.DebugMode)
                {
                    Plugin.Instance.Logger.Debug($"{nameof(EnableProxyServer)} postfix failed: {e.Message}");
                }
            }
        }

        private static void ApplyByReflection(HttpMessageHandler handler, WebProxy proxy, bool ignoreCertificate)
        {
            var type = handler.GetType();

            type.GetProperty("Proxy")?.SetValue(handler, proxy);
            type.GetProperty("UseProxy")?.SetValue(handler, true);

            if (!ignoreCertificate) return;

            var callback = type.GetProperty("ServerCertificateCustomValidationCallback");
            if (callback is null || callback.PropertyType == typeof(object)) return;

            // SocketsHttpHandler.ServerCertificateCustomValidationCallback 形如
            // Func<HttpRequestMessage, X509Certificate2, X509Chain, SslPolicyErrors, bool>
            var parameters = callback.PropertyType.GetGenericArguments();
            if (parameters.Length != 5)
            {
                return;
            }

            var method = typeof(EnableProxyServer).GetMethod(nameof(AlwaysValidCertificate),
                BindingFlags.NonPublic | BindingFlags.Static);

            if (method is null) return;

            try
            {
                callback.SetValue(handler, Delegate.CreateDelegate(callback.PropertyType, method));
            }
            catch (Exception e)
            {
                if (Plugin.Instance.DebugMode)
                {
                    Plugin.Instance.Logger.Debug($"{nameof(EnableProxyServer)} cert callback skipped: {e.Message}");
                }
            }
        }

        private static bool AlwaysValidCertificate(HttpRequestMessage request, X509Certificate2 cert,
            X509Chain chain, SslPolicyErrors errors)
        {
            return true;
        }

        private static NetworkOptions GetNetworkOptions()
        {
            try
            {
                return Plugin.Instance?.GetPluginOptions()?.NetworkOptions;
            }
            catch
            {
                return null;
            }
        }
    }
}
