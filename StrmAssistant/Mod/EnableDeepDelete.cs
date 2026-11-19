using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace StrmAssistant.Mod
{
    /// <summary>
    /// 深度删除（移植自完整社区版 StrmAssistant.Mod.Experience.EnableDeepDelete）。
    ///
    /// 背景：Emby 删除媒体时只会删库记录 + 库目录下的 .strm / 软链文件本身，
    /// 而 STRM 内容指向的挂载目录、软链目标以及它们的字幕 / nfo 会留在磁盘上。
    /// 这里给 LibraryManager.DeleteItem 挂 Prefix + Finalizer：
    ///   1. Prefix 在 Emby 动手删之前，把「真实目标路径」解析出来存进 __state
    ///      （此时媒体源信息还在数据库里，删完就查不到了）；
    ///   2. Finalizer 在原方法成功返回后，把需要级联删除的本地路径丢到后台任务执行，
    ///      删除失败也不影响 Emby 自己的删除结果。
    ///
    /// 安全约束：
    ///   - 只在用户勾选「删除文件」(DeleteFileLocation) 时才工作；
    ///   - 只删 file:// / 本地绝对路径，http(s) 等远程资源只记录不删；
    ///   - 普通媒体文件由 Emby 自己删，这里不重复删；
    ///   - 目标方法找不到或补丁挂载失败时降级为不生效，绝不影响 Emby 正常删除。
    /// </summary>
    internal class EnableDeepDelete : PatchBase<EnableDeepDelete>
    {
        private const string LibraryManagerTypeName = "Emby.Server.Implementations.Library.LibraryManager";
        private const string MethodName = "DeleteItem";

        private static MethodInfo _deleteItem;

        public EnableDeepDelete()
        {
            Initialize();
        }

        protected override void OnInitialize()
        {
            var assembly = Assembly.Load("Emby.Server.Implementations");
            if (assembly is null)
            {
                Plugin.Instance.Logger.Warn($"{nameof(EnableDeepDelete)} - Emby.Server.Implementations not found");
                return;
            }

            var managerType = assembly.GetType(LibraryManagerTypeName);
            if (managerType is null)
            {
                Plugin.Instance.Logger.Warn($"{nameof(EnableDeepDelete)} - {LibraryManagerTypeName} not found");
                return;
            }

            // 优先精确匹配社区版用的 4 参序列（4.8 / 4.9.1.90 / 4.10.1.0 实测一致）：
            // DeleteItem(BaseItem item, DeleteOptions options, BaseItem parent, bool notifyParentItem)。
            // 只按「参数最多」挑会误中将来新增的无关重载。
            var candidates = managerType
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.Name == MethodName)
                .ToList();

            _deleteItem = candidates.FirstOrDefault(m =>
            {
                var parameters = m.GetParameters();
                return parameters.Length == 4 &&
                       parameters[0].ParameterType == typeof(BaseItem) &&
                       parameters[1].ParameterType == typeof(DeleteOptions) &&
                       parameters[2].ParameterType == typeof(BaseItem) &&
                       parameters[3].ParameterType == typeof(bool);
            });

            // Emby 以后在末尾追加参数时，降级到「含必需参数类型且参数最多」的那个，
            // Harmony 前缀按参数名绑定 item / options，仍能正常工作。
            if (_deleteItem is null)
            {
                _deleteItem = candidates
                    .Where(m =>
                    {
                        var parameters = m.GetParameters();
                        return parameters.Any(p => p.ParameterType == typeof(BaseItem)) &&
                               parameters.Any(p => p.ParameterType == typeof(DeleteOptions)) &&
                               parameters.Any(p => p.ParameterType == typeof(bool));
                    })
                    .OrderByDescending(m => m.GetParameters().Length)
                    .FirstOrDefault();
            }

            if (_deleteItem is null)
            {
                Plugin.Instance.Logger.Warn($"{nameof(EnableDeepDelete)} - {MethodName} overload not found");
            }
        }

        /// <summary>按当前选项决定 patch 还是 unpatch；启动时与保存设置时都会调。</summary>
        public void Apply()
        {
            if (GetOptionsSafe()?.EnableDeepDelete == true)
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
            PatchManager.PatchUnpatch(PatchTracker, apply, _deleteItem,
                nameof(DeleteItemPrefix), null, null, nameof(DeleteItemFinalizer));
        }

        /// <summary>
        /// 删除前收集真实目标路径。__state 由 Harmony 传递给同一次调用的 Finalizer。
        /// 这里不能抛异常：前缀抛异常会直接中断 Emby 的删除流程。
        /// </summary>
        [HarmonyPrefix]
        private static void DeleteItemPrefix(ILibraryManager __instance, BaseItem item, DeleteOptions options,
            out Dictionary<string, bool?> __state)
        {
            __state = null;
            if (options?.DeleteFileLocation != true || item is null) return;

            try
            {
                // GetCollectionFolders 返回 Folder[]，DeleteOptions.CollectionFolders 是 BaseItem[]；
                // 数组协变可隐式转换，这里显式声明类型，避免 ?? 推断成 Folder[] 与后面的重载不匹配
                BaseItem[] collectionFolders = options.CollectionFolders ?? __instance.GetCollectionFolders(item);
                var scope = item.GetDeletePaths(true, collectionFolders)
                    .Select(p => p.FullName)
                    .Where(p => !string.IsNullOrEmpty(p))
                    .ToArray();
                __state = Plugin.LibraryApi.PrepareDeepDelete(item, scope);
            }
            catch (Exception e)
            {
                Plugin.Instance.Logger.Warn($"{nameof(EnableDeepDelete)} - Prepare failed: {e.Message}");
                __state = null;
            }
        }

        /// <summary>
        /// 原方法成功返回后才真正删挂载目标。__exception 非空说明 Emby 自己就删失败了，
        /// 此时不追加删除，避免留下半删状态。
        /// </summary>
        [HarmonyFinalizer]
        private static void DeleteItemFinalizer(Exception __exception, BaseItem item, Dictionary<string, bool?> __state)
        {
            if (__state is null || __state.Count == 0 || __exception != null) return;

            var localMountPaths = new HashSet<string>(
                __state.Where(kv => kv.Value ?? false).Select(kv => kv.Key));

            // 通知里要列出全部解析到的挂载路径（含只记录不删的远程资源）；
            // 即使没有本地目标（纯远程 / 纯普通文件）也要发通知，与「删除版本」入口行为一致
            var allMountPaths = new HashSet<string>(__state.Keys);
            var deletedItem = item;

            // 明确 fire-and-forget：删除与通知都不阻塞 Emby 的删除请求
            _ = Task.Run(() =>
            {
                if (localMountPaths.Count > 0)
                {
                    try
                    {
                        Plugin.LibraryApi.ExecuteDeepDelete(localMountPaths);
                    }
                    catch (Exception e)
                    {
                        // 级联删除异常不能掐掉后面的通知
                        Plugin.Instance.Logger.Warn($"{nameof(EnableDeepDelete)} - Execute failed: {e.Message}");
                    }
                }

                // 通知统一走 NotificationApi，与「删除版本」入口共用同一份实现
                Plugin.NotificationApi?.DeepDeleteSendNotificationToAdmins(deletedItem, allMountPaths);
            });
        }

        internal static Options.ExperienceEnhanceOptions GetOptionsSafe()
        {
            try
            {
                return Plugin.Instance?.GetPluginOptions()?.ExperienceEnhanceOptions;
            }
            catch
            {
                return null;
            }
        }
    }
}
