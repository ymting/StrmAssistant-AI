using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Validation;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.LocalizationAttributes;
using StrmAssistant.Common;
using StrmAssistant.Properties;
using System.ComponentModel;

namespace StrmAssistant.Options
{
    /// <summary>
    /// 网络选项：让 Emby 出站请求走代理。
    /// 文案沿用社区原版的资源键（Resources.resx 里本来就带完整版的中文翻译）。
    /// </summary>
    public class NetworkOptions : EditableOptionsBase
    {
        public override string EditorTitle => Resources.NetworkOptions_EditorTitle_Network;

        [DisplayNameL("NetworkOptions_EnableProxyServer_Enable_Proxy_Server", typeof(Resources))]
        [DescriptionL("NetworkOptions_EnableProxyServer_Enable_Proxy_Server__Default_is_OFF_", typeof(Resources))]
        public bool EnableProxyServer { get; set; }

        [DisplayNameL("ModOptions_ProxyServer_Proxy_Server", typeof(Resources))]
        [DescriptionL("ModOptions_ProxyServer_Enable_http_proxy_server__Blank_is_OFF_", typeof(Resources))]
        [VisibleCondition(nameof(EnableProxyServer), SimpleCondition.IsTrue)]
        public string ProxyServerUrl { get; set; } = string.Empty;

        [VisibleCondition(nameof(EnableProxyServer), SimpleCondition.IsTrue)]
        public StatusItem ProxyServerStatus { get; set; } = new StatusItem();

        [DisplayName("忽略证书校验")]
        [Description("仅当代理做中间人解密时才需要打开，默认关闭。")]
        [VisibleCondition(nameof(EnableProxyServer), SimpleCondition.IsTrue)]
        public bool IgnoreCertificateValidation { get; set; }

        protected override void Validate(ValidationContext context)
        {
            if (!string.IsNullOrWhiteSpace(ProxyServerUrl) && !CommonUtility.IsValidProxyUrl(ProxyServerUrl))
            {
                context.AddValidationError("代理服务器地址格式不正确，应形如 http://host:port。");
            }
        }

        public void ResetStatus()
        {
            ProxyServerStatus.Caption = string.Empty;
            ProxyServerStatus.StatusText = string.Empty;
            ProxyServerStatus.Status = ItemStatus.None;
        }
    }
}
