using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Elements.List;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.LocalizationAttributes;
using StrmAssistant.Properties;
using System.ComponentModel;

namespace StrmAssistant.Options
{
    public class ExperienceEnhanceOptions : EditableOptionsBase
    {
        [DisplayNameL("ExperienceEnhanceOptions_EditorTitle_Experience_Enhance", typeof(Resources))]
        public override string EditorTitle => Resources.ExperienceEnhanceOptions_EditorTitle_Experience_Enhance;
        
        [DisplayNameL("GeneralOptions_MergeMultiVersion_Merge_Multiple_Versions", typeof(Resources))]
        [DescriptionL("GeneralOptions_MergeMultiVersion_Auto_merge_multiple_versions_if_in_the_same_folder_", typeof(Resources))]
        [Required]
        public bool MergeMultiVersion { get; set; } = false;

        public enum MergeMoviesScopeOption
        {
            [DescriptionL("MergeScopeOption_LibraryScope_LibraryScope", typeof(Resources))]
            LibraryScope,
            [DescriptionL("MergeScopeOption_GlobalScope_GlobalScope", typeof(Resources))]
            GlobalScope
        }
        
        [DisplayName("")]
        [VisibleCondition(nameof(MergeMultiVersion), SimpleCondition.IsTrue)]
        public MergeMoviesScopeOption MergeMoviesPreference { get; set; } = MergeMoviesScopeOption.LibraryScope;

        public enum MergeSeriesScopeOption
        {
            [DescriptionL("MergeScopeOption_LibraryScope_LibraryScope", typeof(Resources))]
            LibraryScope,
            [DescriptionL("MergeScopeOption_GlobalScope_GlobalScope", typeof(Resources))]
            GlobalScope
        }

        [VisibleCondition(nameof(MergeMultiVersion), SimpleCondition.IsTrue)]
        [Browsable(false)]
        public MergeSeriesScopeOption MergeSeriesPreference => MergeSeriesScopeOption.LibraryScope;

        /// <summary>
        /// 阻止指定插件被在线自动更新。填插件名（MovieDb）或 dll 文件名（MovieDb.dll），
        /// 逗号 / 分号分隔。移植自完整社区版，只拦 Emby 的「检查插件更新」列表，手动安装不受影响。
        /// </summary>
        [DisplayNameL("ExperienceEnhanceOptions_SuppressPluginUpdates_Suppress_Auto_Plugin_Updates", typeof(Resources))]
        [DescriptionL(
            "ExperienceEnhanceOptions_SuppressPluginUpdates_Plugin_names_separated_by_comma_or_semicolon_like_MovieDb_Tvdb__Default_is_BLANK_",
            typeof(Resources))]
        public string SuppressPluginUpdates { get; set; } = string.Empty;

        /// <summary>设置页展示用：命中哪些已装插件、哪些条目压根没匹配上（多半是拼错了）。</summary>
        [DisplayName("插件更新屏蔽状态")]
        public GenericItemList SuppressPluginUpdateStatus { get; set; } = new GenericItemList();
    }
}
