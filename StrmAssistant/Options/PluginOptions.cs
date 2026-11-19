using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.List;
using Emby.Web.GenericEdit.Validation;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.LocalizationAttributes;
using StrmAssistant.Properties;
using System;
using System.ComponentModel;
using System.Linq;
using static StrmAssistant.Options.GeneralOptions;

namespace StrmAssistant.Options
{
    public class PluginOptions : EditableOptionsBase
    {
        public override string EditorTitle => Resources.PluginOptions_EditorTitle_Strm_Assistant;

        public override string EditorDescription => string.Empty;
        
        public GenericItemList Disclaimer { get; set; } = new GenericItemList();

        [VisibleCondition(nameof(ShowConflictPluginLoadedStatus), SimpleCondition.IsTrue)]
        public StatusItem ConflictPluginLoadedStatus { get; set; } = new StatusItem();

        [VisibleCondition(nameof(IsModSuccess), SimpleCondition.IsFalse)]
        public StatusItem ModStatus { get; set; } = new StatusItem();

        [DisplayName("第三方插件")]
        public GenericItemList ThirdPartyPluginStatus { get; set; } = new GenericItemList();

        // 区块按「基础 → 媒体处理 → 搜索与界面 → 体验与关于」四组相邻排列，
        // 让相关设置在同一屏里连在一起；仅调整声明顺序，不改变任何字段或 XML 结构。
        //
        // 一、基础
        [DisplayNameL("GeneralOptions_EditorTitle_General_Options", typeof(Resources))]
        public GeneralOptions GeneralOptions { get; set; } = new GeneralOptions();

        [DisplayNameL("NetworkOptions_EditorTitle_Network", typeof(Resources))]
        public NetworkOptions NetworkOptions { get; set; } = new NetworkOptions();

        // 二、媒体处理
        [DisplayNameL("PluginOptions_EditorTitle_Strm_Extract", typeof(Resources))]
        public MediaInfoExtractOptions MediaInfoExtractOptions { get; set; } = new MediaInfoExtractOptions();

        [DisplayNameL("PluginOptions_MetadataEnhanceOptions_Metadata_Enhance", typeof(Resources))]
        public MetadataEnhanceOptions MetadataEnhanceOptions { get; set; } = new MetadataEnhanceOptions();

        [DisplayNameL("PluginOptions_IntroSkipOptions_Intro_Credits_Detection", typeof(Resources))]
        public IntroSkipOptions IntroSkipOptions { get; set; } = new IntroSkipOptions();

        // 三、搜索与界面
        [DisplayNameL("SearchEnhanceOptions_EditorTitle_Grouped", typeof(Resources))]
        public SearchEnhanceOptions SearchEnhanceOptions { get; set; } = new SearchEnhanceOptions();

        // UI 功能：目前只有「隐藏合集媒体库」一项，移植自完整社区版 UIFunctionOptions
        [DisplayNameL("UIFunctionOptions_EditorTitle_UI_Functions", typeof(Resources))]
        public UIFunctionOptions UIFunctionOptions { get; set; } = new UIFunctionOptions();

        // 四、体验与关于
        [DisplayNameL("ExperienceEnhanceOptions_EditorTitle_Experience_Enhance", typeof(Resources))]
        public ExperienceEnhanceOptions ExperienceEnhanceOptions { get; set; } = new ExperienceEnhanceOptions();

        [DisplayNameL("AboutOptions_EditorTitle_About", typeof(Resources))]
        public AboutOptions AboutOptions { get; set; } = new AboutOptions();

        [Browsable(false)]
        public bool? IsModSuccess => true;

        [Browsable(false)]
        public bool ShowConflictPluginLoadedStatus =>
            AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetName().Name)
                .Any(n => n == "StrmExtract" || n == "InfuseSync");

        protected override void Validate(ValidationContext context)
        {
            if (GeneralOptions.CatchupMode &&
                GeneralOptions.CatchupTaskScope.Contains(CatchupTask.Fingerprint.ToString()) &&
                !IntroSkipOptions.UnlockIntroSkip)
            {
                context.AddValidationError(Resources.InvalidFingerprintCatchup);
            }
        }
    }
}
