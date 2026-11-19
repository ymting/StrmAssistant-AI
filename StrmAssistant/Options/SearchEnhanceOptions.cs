using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Common;
using Emby.Web.GenericEdit.Elements.List;
using MediaBrowser.Model.Attributes;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace StrmAssistant.Options
{
    /// <summary>搜索增强的范围：只影响「按标题搜索」时默认纳入哪些条目类型。</summary>
    public enum SearchItemType
    {
        Movie,
        Collection,
        Series,
        Season,
        Episode,
        Person,
        Video,
        MusicVideo,
        Audio,
        BoxSet,
        Trailer,
        Game,
        Tag,
        Studio
    }

    /// <summary>
    /// 中文搜索增强选项（移植自完整社区版 ModOptions 中的搜索相关部分）。
    ///
    /// 需要注意：开启后 FTS 表 tokenizer 会被切换为自定义的 simple，
    /// 关闭时必须走「恢复」流程把 tokenizer 换回 unicode61 并重建索引，
    /// 否则会留下「表指向不存在的 tokenizer」的坏状态。
    /// </summary>
    public class SearchEnhanceOptions : EditableOptionsBase
    {
        public override string EditorTitle => "Chinese Search";

        [DisplayName("Enhance Chinese Search")]
        [Description("Chinese fuzzy search and Pinyin search. Requires libsimple.so, which is exported automatically. Default is OFF.")]
        [Required]
        public bool EnhanceChineseSearch { get; set; }

        /// <summary>由代码内部使用：置 true 表示下次启动执行「恢复官方 tokenizer」。</summary>
        [Browsable(false)]
        public bool EnhanceChineseSearchRestore { get; set; }

        [DisplayName("Exclude Original Title")]
        [Description("Exclude OriginalTitle from search results. Default is OFF.")]
        [Required]
        [VisibleCondition(nameof(EnhanceChineseSearch), SimpleCondition.IsTrue)]
        public bool ExcludeOriginalTitleFromSearch { get; set; }

        [DisplayName("Search Scope")]
        [Description("Item types included when searching by title. Blank means all.")]
        [EditMultilSelect]
        [SelectItemsSource("SearchItemTypeList")]
        [VisibleCondition(nameof(EnhanceChineseSearch), SimpleCondition.IsTrue)]
        public string SearchScope { get; set; } =
            string.Join(",", new[] { SearchItemType.Movie, SearchItemType.Collection, SearchItemType.Series });

        [Browsable(false)]
        public List<EditorSelectOption> SearchItemTypeList { get; set; } = new List<EditorSelectOption>();

        public void Initialize()
        {
            SearchItemTypeList.Clear();

            foreach (SearchItemType value in Enum.GetValues(typeof(SearchItemType)))
            {
                SearchItemTypeList.Add(new EditorSelectOption
                {
                    Value = value.ToString(),
                    Name = value.ToString(),
                    IsEnabled = true
                });
            }
        }

        /// <summary>
        /// 当前 Emby 版本是否允许开启。完整版把上限定在 4.9.0.48，
        /// 本分支实测 4.9.1.90 的 FTS 表名（fts_search9）与四个 patch 方法都还在，因此放宽到 4.10 以内，
        /// 但仍标注为「未验证」，出问题可直接关开关回滚。
        /// </summary>
        [Browsable(false)]
        public bool IsChineseSearchSupported
        {
            get
            {
                var appVer = Utility.AppVer;

                if (appVer >= Utility.Ver4830 && appVer < Utility.Ver4900) return true;
                if (appVer >= Utility.Ver49015 && appVer < new Version("4.11.0.0")) return true;

                return false;
            }
        }
    }
}
