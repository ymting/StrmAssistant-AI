using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Validation;
using MediaBrowser.Model.Attributes;
using StrmAssistant.Common;
using System.ComponentModel;

namespace StrmAssistant.Options
{
    /// <summary>
    /// 网络选项：让 Emby 出站请求走代理。
    /// 移植自完整社区版 NetworkOptions，保留配置项与校验，状态展示由 Plugin 在打开设置页时刷新。
    /// </summary>
    public class NetworkOptions : EditableOptionsBase
    {
        public override string EditorTitle => "Network";

        [DisplayName("Enable Proxy Server")]
        [Description("Route Emby outbound requests (TMDB / Douban / plugin downloads) through the proxy below. Default is OFF.")]
        public bool EnableProxyServer { get; set; }

        [DisplayName("Proxy Server")]
        [Description("http://host:port, https://host:port or socks5://host:port. Blank means OFF.")]
        [VisibleCondition(nameof(EnableProxyServer), SimpleCondition.IsTrue)]
        public string ProxyServerUrl { get; set; } = string.Empty;

        [VisibleCondition(nameof(EnableProxyServer), SimpleCondition.IsTrue)]
        public StatusItem ProxyServerStatus { get; set; } = new StatusItem();

        [DisplayName("Ignore Certificate Validation")]
        [Description("Skip TLS certificate validation for proxied requests. Only enable when your proxy does MITM.")]
        [VisibleCondition(nameof(EnableProxyServer), SimpleCondition.IsTrue)]
        public bool IgnoreCertificateValidation { get; set; }

        protected override void Validate(ValidationContext context)
        {
            if (!string.IsNullOrWhiteSpace(ProxyServerUrl) && !CommonUtility.IsValidProxyUrl(ProxyServerUrl))
            {
                context.AddValidationError("Invalid proxy server url.");
            }
        }

        public void ResetStatus()
        {
            ProxyServerStatus.StatusText = string.Empty;
            ProxyServerStatus.Status = ItemStatus.None;
        }
    }
}
