using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Common;
using Emby.Web.GenericEdit.Elements.List;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.LocalizationAttributes;
using StrmAssistant.Properties;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace StrmAssistant.Options
{
    /// <summary>
    /// 搜索增强的范围。
    /// 枚举名必须是 Emby 认识的英文类型名（会直接作为 IncludeItemTypes 传给查询），
    /// 只在界面上显示中文。
    /// </summary>
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
    /// 中文搜索增强选项（移植自完整社区版 ModOptions 的搜索部分）。
    /// 文案沿用社区原版的资源键。
    ///
    /// 注意：开启后 FTS 表 tokenizer 会切到自定义的 simple，
    /// 关闭时必须走「恢复」流程换回 unicode61 并重建索引，
    /// 否则会留下「表指向不存在的 tokenizer」的坏状态。
    /// </summary>
    public class SearchEnhanceOptions : EditableOptionsBase
    {
        public override string EditorTitle => "中文搜索";

        [DisplayNameL("ModOptions_EnhanceChineseSearch_Enhance_Chinese_Search", typeof(Resources))]
        [DescriptionL("ModOptions_EnhanceChineseSearch_Support_Chinese_fuzzy_search_and_Pinyin_search__Default_is_OFF_", typeof(Resources))]
        [Required]
        public bool EnhanceChineseSearch { get; set; }

        /// <summary>由代码内部使用：置 true 表示下次启动执行「恢复官方 tokenizer」。</summary>
        [Browsable(false)]
        public bool EnhanceChineseSearchRestore { get; set; }

        [DisplayNameL("ModOptions_ExcludeOriginalTitle_Exclude_Original_Title", typeof(Resources))]
        [DescriptionL("ModOptions_ExcludeOriginalTitle_Exclude_original_title_from_search__Default_is_OFF_", typeof(Resources))]
        [Required]
        [VisibleCondition(nameof(EnhanceChineseSearch), SimpleCondition.IsTrue)]
        public bool ExcludeOriginalTitleFromSearch { get; set; }

        [DisplayNameL("ModOptions_SearchScope_Search_Scope", typeof(Resources))]
        [DescriptionL("ModOptions_SearchScope_Include_item_types__Blank_includes_all_", typeof(Resources))]
        [EditMultilSelect]
        [SelectItemsSource("SearchItemTypeList")]
        [VisibleCondition(nameof(EnhanceChineseSearch), SimpleCondition.IsTrue)]
        public string SearchScope { get; set; } =
            string.Join(",", new[] { SearchItemType.Movie, SearchItemType.Collection, SearchItemType.Series });

        [Browsable(false)]
        public List<EditorSelectOption> SearchItemTypeList { get; set; } = new List<EditorSelectOption>();

        /// <summary>界面显示用的中文名，枚举值保持 Emby 认识的英文类型名。</summary>
        private static readonly Dictionary<SearchItemType, string> DisplayNames =
            new Dictionary<SearchItemType, string>
            {
                { SearchItemType.Movie, "电影" },
                { SearchItemType.Collection, "合集" },
                { SearchItemType.Series, "电视剧" },
                { SearchItemType.Season, "季" },
                { SearchItemType.Episode, "集" },
                { SearchItemType.Person, "人物" },
                { SearchItemType.Video, "视频" },
                { SearchItemType.MusicVideo, "音乐视频" },
                { SearchItemType.Audio, "音频" },
                { SearchItemType.BoxSet, "合集盒" },
                { SearchItemType.Trailer, "预告片" },
                { SearchItemType.Game, "游戏" },
                { SearchItemType.Tag, "标签" },
                { SearchItemType.Studio, "工作室" }
            };

        public void Initialize()
        {
            SearchItemTypeList.Clear();

            foreach (SearchItemType value in Enum.GetValues(typeof(SearchItemType)))
            {
                SearchItemTypeList.Add(new EditorSelectOption
                {
                    Value = value.ToString(),
                    Name = DisplayNames.TryGetValue(value, out var name) ? name : value.ToString(),
                    IsEnabled = true
                });
            }
        }

        /// <summary>
        /// 当前 Emby 版本是否允许开启。完整版把上限定在 4.9.0.48；
        /// 本分支实测 4.10.1.0 的 FTS 表与四个 patch 方法都还在，因此放宽到 4.11 以内。
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
