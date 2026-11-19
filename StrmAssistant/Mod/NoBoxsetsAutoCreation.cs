using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Library;
using System;
using System.Linq;
using System.Reflection;

namespace StrmAssistant.Mod
{
    /// <summary>
    /// 隐藏「合集」媒体库，并阻止 Emby 扫库时自动把它建回来（移植自完整社区版
    /// StrmAssistant.Mod.UIFunction.NoBoxsetsAutoCreation）。
    ///
    /// 两个拦截点：
    ///   1. CollectionManager.EnsureLibraryFolder —— 前缀直接返回 false，让 Emby 建不出合集库；
    ///      对已经存在的合集库无副作用，只是不再自动新建。
    ///   2. UserViewManager.GetUserViews —— 前缀把结果里的 CollectionType == boxsets 的库剔除，
    ///      用户界面上看不到（不影响服务端对 BoxSet 条目的管理）。
    ///
    /// 不直接引用 Emby.Server.Implementations.dll（内部程序集，版本间可能变），按类型名反射拿方法；
    /// 任一目标拿不到就整体降级为 None，不影响插件其它功能。
    /// 开关即时生效：保存设置时调用 Apply()，启动初始化时也调用。
    /// </summary>
    internal class NoBoxsetsAutoCreation : PatchBase<NoBoxsetsAutoCreation>
    {
        private const string CollectionManagerTypeName =
            "Emby.Server.Implementations.Collections.CollectionManager";
        private const string UserViewManagerTypeName =
            "Emby.Server.Implementations.Library.UserViewManager";

        private static MethodInfo _ensureLibraryFolder;
        private static MethodInfo _getUserViews;

        public NoBoxsetsAutoCreation()
        {
            Initialize();
        }

        protected override void OnInitialize()
        {
            var assembly = Assembly.Load("Emby.Server.Implementations");
            if (assembly is null)
            {
                Plugin.Instance.Logger.Warn($"{nameof(NoBoxsetsAutoCreation)} - Emby.Server.Implementations not found");
                return;
            }

            var collectionManager = assembly.GetType(CollectionManagerTypeName);
            var userViewManager = assembly.GetType(UserViewManagerTypeName);

            if (collectionManager is null || userViewManager is null)
            {
                Plugin.Instance.Logger.Warn(
                    $"{nameof(NoBoxsetsAutoCreation)} - {CollectionManagerTypeName} / {UserViewManagerTypeName} not found");
                return;
            }

            _ensureLibraryFolder = AccessTools.Method(collectionManager, "EnsureLibraryFolder");

            // GetUserViews 在 4.9 / 4.10 有三个重载，隐藏合集库要挂在参数最多、
            // 且包含 query/user/folders 的那个（社区版就是按参数最多选）。
            // prefix 的 query/user/folders 三个名字与它一一对应，Harmony 按名字绑定。
            _getUserViews = userViewManager
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.Name == "GetUserViews")
                .OrderByDescending(m => m.GetParameters().Length)
                .FirstOrDefault();

            if (_ensureLibraryFolder is null || _getUserViews is null)
            {
                Plugin.Instance.Logger.Warn($"{nameof(NoBoxsetsAutoCreation)} - target method not found");
            }
        }

        /// <summary>按当前选项决定 patch 还是 unpatch，启动时与保存设置时都会调。</summary>
        public void Apply()
        {
            if (GetOptionsSafe()?.NoBoxsetsAutoCreation == true)
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
            PatchManager.PatchUnpatch(PatchTracker, apply, _ensureLibraryFolder,
                nameof(EnsureLibraryFolderPrefix));
            PatchManager.PatchUnpatch(PatchTracker, apply, _getUserViews,
                nameof(GetUserViewsPrefix));
        }

        /// <summary>拦住合集库的自动创建：前缀直接返回 false 跳过原方法。</summary>
        [HarmonyPrefix]
        private static bool EnsureLibraryFolderPrefix()
        {
            return false;
        }

        /// <summary>把合集库从用户视图里剔除。folders 是原方法参数（ref），改完直接生效。</summary>
        [HarmonyPrefix]
        private static void GetUserViewsPrefix(UserViewQuery query, User user, ref Folder[] folders)
        {
            if (folders is null) return;

            folders = folders.Where(f =>
            {
                var collectionFolder = f as CollectionFolder;
                return collectionFolder is null ||
                       collectionFolder.CollectionType != CollectionType.BoxSets.ToString();
            }).ToArray();
        }

        internal static Options.UIFunctionOptions GetOptionsSafe()
        {
            try
            {
                return Plugin.Instance?.GetPluginOptions()?.UIFunctionOptions;
            }
            catch
            {
                return null;
            }
        }
    }
}
