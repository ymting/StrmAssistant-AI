using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace StrmAssistant.Common
{
    /// <summary>
    /// 第三方 Emby 插件探测。
    ///
    /// 本插件与第三方插件的关系分两种：
    /// 1. 有真实代码依赖的（MovieDb 的演职人员 provider）——需要按能力解析，不能只认类型名，
    ///    否则 MovieDb 一改类名（1.x -> 2.x 就很可能改）功能会静默失效；
    /// 2. 只有冲突关系或纯信息参考的（InfuseSync / StrmExtract / OMDb / OpenSubtitles / DataExplorer2）
    ///    ——需求是「装没装、什么版本、会不会互相影响」，展示出来即可，不做魔改。
    /// </summary>
    internal static class ThirdPartyPluginApi
    {
        internal class PluginInfo
        {
            public string Name { get; set; }
            public string Version { get; set; }
            public bool IsLoaded { get; set; }
            public string Note { get; set; }
            public bool IsWarning { get; set; }
        }

        private static readonly (string AssemblyName, string DisplayName, string Note)[] KnownPlugins =
        {
            ("MovieDb", "MovieDb", "演职人员增强 / 原语言海报"),
            ("InfuseSync", "InfuseSync", "存在已知线程安全冲突，建议不与本插件同时启用魔改类功能"),
            ("StrmExtract", "StrmExtract", "与本插件功能重叠，不能同时安装"),
            ("OMDb", "OMDb", "评分来源，与中文元数据增强可能互相覆盖"),
            ("OpenSubtitles", "OpenSubtitles", "外挂字幕下载源，本插件只做本地扫描，不冲突"),
            ("Emby.DataExplorer2", "DataExplorer2", "用于注入右键菜单命令")
        };

        private static readonly object SyncRoot = new object();
        private static IMetadataProvider _movieDbPersonProvider;
        private static bool _movieDbWarned;

        /// <summary>
        /// 已加载的插件清单。只能看到「已被 Emby 加载」的插件，未启用的不会出现在这里。
        /// </summary>
        public static List<PluginInfo> GetLoadedPlugins()
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();

            return KnownPlugins.Select(known =>
            {
                var assembly = assemblies.FirstOrDefault(a =>
                    string.Equals(a.GetName().Name, known.AssemblyName, StringComparison.OrdinalIgnoreCase));

                return new PluginInfo
                {
                    Name = known.DisplayName,
                    Version = assembly?.GetName().Version?.ToString() ?? "-",
                    IsLoaded = assembly != null,
                    Note = known.Note,
                    IsWarning = known.AssemblyName == "InfuseSync" || known.AssemblyName == "StrmExtract"
                };
            }).ToList();
        }

        /// <summary>
        /// 解析 MovieDb 的演职人员 provider。
        ///
        /// 匹配顺序（从强到弱），任何一条命中即可，避免 MovieDb 升级后改类名导致功能静默失效：
        /// 1. 类型名包含 MovieDb 且实现 IRemoteMetadataProvider&lt;Person, PersonLookupInfo&gt;
        /// 2. 所在程序集名包含 MovieDb（2.x 若把 provider 拆到子命名空间/子类型仍能命中）
        /// 3. Provider 显示名包含 MovieDb / TheMovieDb
        ///
        /// 解析成功才缓存：Emby 的插件加载顺序不保证 MovieDb 一定早于本插件，
        /// 失败时保留重试机会，避免「第一次没找到就永远找不到」。
        /// </summary>
        public static IMetadataProvider ResolveMovieDbPersonProvider()
        {
            lock (SyncRoot)
            {
                if (_movieDbPersonProvider != null) return _movieDbPersonProvider;

                try
                {
                    var providers = Plugin.Instance.ApplicationHost.GetExports<IMetadataProvider>()
                        .Where(p => p is IRemoteMetadataProvider<Person, PersonLookupInfo>)
                        .ToList();

                    _movieDbPersonProvider =
                        providers.FirstOrDefault(p =>
                            p.GetType().Name.IndexOf("MovieDb", StringComparison.OrdinalIgnoreCase) >= 0) ??
                        providers.FirstOrDefault(p =>
                            (p.GetType().Assembly.GetName().Name ?? string.Empty)
                            .IndexOf("MovieDb", StringComparison.OrdinalIgnoreCase) >= 0) ??
                        providers.FirstOrDefault(p =>
                            (p.Name ?? string.Empty).IndexOf("MovieDb", StringComparison.OrdinalIgnoreCase) >= 0);

                    if (_movieDbPersonProvider is null && !_movieDbWarned)
                    {
                        _movieDbWarned = true;

                        var movieDbLoaded = AppDomain.CurrentDomain.GetAssemblies()
                            .Any(a => string.Equals(a.GetName().Name, "MovieDb", StringComparison.OrdinalIgnoreCase));

                        Plugin.Instance.Logger.Warn(movieDbLoaded
                            ? "MovieDb is loaded but no Person metadata provider matched. " +
                              "Person enhancement is skipped."
                            : "MovieDb plugin is not loaded. Person enhancement is unavailable.");
                    }
                    else if (_movieDbPersonProvider != null && Plugin.Instance.DebugMode)
                    {
                        Plugin.Instance.Logger.Debug(
                            $"{nameof(ThirdPartyPluginApi)} - MovieDb person provider: " +
                            $"{_movieDbPersonProvider.GetType().FullName} " +
                            $"({_movieDbPersonProvider.GetType().Assembly.GetName().Version})");
                    }
                }
                catch (Exception e)
                {
                    Plugin.Instance.Logger.Warn($"{nameof(ThirdPartyPluginApi)} Init Failed: {e.Message}");
                }

                return _movieDbPersonProvider;
            }
        }

        /// <summary>
        /// 设置页保存时调用，让 MovieDb 刚刚启用/升级的场景能重新解析。
        /// </summary>
        public static void ResetCache()
        {
            lock (SyncRoot)
            {
                _movieDbPersonProvider = null;
                _movieDbWarned = false;
            }
        }

        public static string DescribeLoadedPlugins()
        {
            var loaded = GetLoadedPlugins().Where(p => p.IsLoaded).ToList();

            return loaded.Any()
                ? string.Join(", ", loaded.Select(p => $"{p.Name} {p.Version}"))
                : "NONE";
        }
    }
}
