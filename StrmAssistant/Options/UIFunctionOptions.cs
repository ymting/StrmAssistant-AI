using Emby.Web.GenericEdit;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.LocalizationAttributes;
using StrmAssistant.Properties;

namespace StrmAssistant.Options
{
    /// <summary>
    /// UI 功能设置。当前只移植了完整社区版里的「隐藏合集媒体库 / 阻止合集库自动创建」，
    /// 其余 UI 功能（美化标题、强制媒体库排序等）本分支没有实现，不在这里暴露开关。
    /// </summary>
    public class UIFunctionOptions : EditableOptionsBase
    {
        [DisplayNameL("UIFunctionOptions_EditorTitle_UI_Functions", typeof(Resources))]
        public override string EditorTitle => Resources.UIFunctionOptions_EditorTitle_UI_Functions;

        [DisplayNameL("UIFunctionOptions_NoBoxsetsAutoCreation_Prevent_Boxsets_Auto_Creation", typeof(Resources))]
        [DescriptionL(
            "UIFunctionOptions_NoBoxsetsAutoCreation_Prevent_automatic_boxset_library_creation__Default_is_OFF_",
            typeof(Resources))]
        [Required]
        public bool NoBoxsetsAutoCreation { get; set; } = false;
    }
}
