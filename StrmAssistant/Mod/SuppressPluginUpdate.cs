using HarmonyLib;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Updates;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace StrmAssistant.Mod
{
    /// <summary>
    /// 阻止指定插件被自动在线更新（移植自完整社区版 StrmAssistant.Mod.Experience.SuppressPluginUpdate）。
    ///
    /// 原理：Emby 的「检查插件更新」计划任务（Emby.Server.Implementations.ScheduledTasks.PluginUpdateTask）
    /// 与插件管理页的「更新」列表，都先调用 InstallationManager.GetAvailablePluginUpdates(...) 拿候选包，
    /// 再逐个 InstallPackage。这里给该方法挂 Postfix，把命中的条目从结果里摘掉，后续安装自然不会发生。
    ///
    /// 影响范围：只拦「在线更新候选列表」。手动上传 dll 安装、手动在插件页点更新某个插件、
    /// 以及 Emby 服务端自身更新都不受影响。
    /// </summary>
    internal class SuppressPluginUpdate : PatchBase<SuppressPluginUpdate>
    {
        private const string InstallationManagerTypeName = "Emby.Server.Implementations.Updates.InstallationManager";
        private const string MethodName = "GetAvailablePluginUpdates";

        private static readonly char[] Separators = { ',', ';' };

        private static MethodInfo _getAvailablePluginUpdates;

        public SuppressPluginUpdate()
        {
            Initialize();
        }

        protected override void OnInitialize()
        {
            // 不直接引用 Emby.Server.Implementations.dll（内部程序集，版本间可能变），按类型名反射拿
            var assembly = Assembly.Load("Emby.Server.Implementations");
            var managerType = assembly?.GetType(InstallationManagerTypeName);

            if (managerType is null)
            {
                Plugin.Instance.Logger.Warn($"{nameof(SuppressPluginUpdate)} - {InstallationManagerTypeName} not found");
                return;
            }

            // 4.9 / 4.10 有两个重载：GetAvailablePluginUpdates(Version, CancellationToken)
            // 和 GetAvailablePluginUpdates(Version, LogSeverity, CancellationToken)。
            // 前者只是转调后者，而计划任务用的正是三参版本，因此取参数最多的那个挂一条补丁即可全覆盖。
            _getAvailablePluginUpdates = managerType
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.Name == MethodName)
                .OrderByDescending(m => m.GetParameters().Length)
                .FirstOrDefault();

            if (_getAvailablePluginUpdates is null)
            {
                Plugin.Instance.Logger.Warn($"{nameof(SuppressPluginUpdate)} - {MethodName} not found");
            }
        }

        /// <summary>按当前选项决定 patch 还是 unpatch；启动时与保存设置时都会调。</summary>
        public void Apply()
        {
            if (GetSuppressedNames().Count > 0)
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
            PatchManager.PatchUnpatch(PatchTracker, apply, _getAvailablePluginUpdates, null,
                nameof(GetAvailablePluginUpdatesPostfix));
        }

        [HarmonyPostfix]
        private static void GetAvailablePluginUpdatesPostfix(ref Task<PackageVersionInfo[]> __result)
        {
            var suppressed = GetSuppressedNames();
            if (suppressed.Count == 0 || __result is null) return;

            __result = FilterAsync(__result, suppressed);
        }

        /// <summary>
        /// 原方法是 async 的，Postfix 回来时 Task 通常还没跑完，这里把过滤接在 await 之后，
        /// 避免用 .Result 同步阻塞请求线程（完整社区版是直接 .Result，理论上会卡住调用线程）。
        /// 原任务抛异常时照常往外抛，保留 Emby 自己的错误处理与日志。
        /// </summary>
        private static async Task<PackageVersionInfo[]> FilterAsync(Task<PackageVersionInfo[]> task,
            HashSet<string> suppressed)
        {
            var updates = await task.ConfigureAwait(false);
            if (updates is null) return Array.Empty<PackageVersionInfo>();

            var kept = updates.Where(p => !IsSuppressed(p, suppressed)).ToArray();

            if (Plugin.Instance?.DebugMode == true && kept.Length != updates.Length)
            {
                Plugin.Instance.Logger.Debug(
                    $"{nameof(SuppressPluginUpdate)} - skipped {updates.Length - kept.Length} plugin update(s)");
            }

            return kept;
        }

        /// <summary>
        /// 一个条目算命中，只要它的包名、插件 GUID、目标文件名（带或不带扩展名）任一与配置相同。
        /// 兼容三种写法：MovieDb、MovieDb.dll、以及直接填 GUID。
        /// </summary>
        private static bool IsSuppressed(PackageVersionInfo package, HashSet<string> suppressed)
        {
            if (package is null) return false;

            return Matches(suppressed, package.name)
                   || Matches(suppressed, package.guid)
                   || Matches(suppressed, package.targetFilename)
                   || Matches(suppressed, Path.GetFileNameWithoutExtension(package.targetFilename));
        }

        private static bool Matches(HashSet<string> suppressed, string value)
        {
            // HashSet 的比较器是 OrdinalIgnoreCase，传 null 会抛，必须先判空
            return !string.IsNullOrWhiteSpace(value) && suppressed.Contains(value);
        }

        internal static HashSet<string> GetSuppressedNames()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var raw = GetOptionsSafe()?.SuppressPluginUpdates;

            if (string.IsNullOrWhiteSpace(raw)) return set;

            foreach (var item in raw.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = item.Trim();
                if (trimmed.Length > 0) set.Add(trimmed);
            }

            return set;
        }

        /// <summary>
        /// 判断已安装插件是否命中某个条目。插件侧能拿来比的是三项：
        /// 显示名（MovieDb）、GUID、程序集文件名（MovieDb.dll / MovieDb）。
        /// </summary>
        internal static bool PluginMatches(IPlugin plugin, string token)
        {
            if (plugin is null || string.IsNullOrWhiteSpace(token)) return false;

            var fileName = Path.GetFileName(plugin.AssemblyFilePath ?? string.Empty);

            return string.Equals(plugin.Name, token, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(plugin.Id.ToString("D"), token, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(fileName, token, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(Path.GetFileNameWithoutExtension(fileName), token,
                       StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>本插件自己也走 UpdatePluginTask，屏蔽名单里包含它时应当同样跳过。</summary>
        internal static bool IsSelfSuppressed()
        {
            var names = GetSuppressedNames();
            return names.Any(n => PluginMatches(Plugin.Instance, n));
        }

        /// <summary>
        /// 给设置页用：把用户填的每一项对应到已安装插件上。
        /// matched 是「命中的插件 名称 版本」，unmatched 是「填了但没有任何插件匹配的条目」——
        /// 后者直接暴露拼写错误，省得用户改了名字却以为在生效。
        /// </summary>
        internal static void DescribeMatches(string raw, out List<string> matched, out List<string> unmatched)
        {
            matched = new List<string>();
            unmatched = new List<string>();

            if (string.IsNullOrWhiteSpace(raw)) return;

            var tokens = raw.Split(Separators, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim())
                .Where(t => t.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var plugins = GetInstalledPlugins();

            foreach (var plugin in plugins)
            {
                var hit = tokens.Any(t => PluginMatches(plugin, t));
                if (hit) matched.Add($"{plugin.Name} {plugin.Version}");
            }

            foreach (var token in tokens)
            {
                if (!plugins.Any(p => PluginMatches(p, token))) unmatched.Add(token);
            }
        }

        private static List<IPlugin> GetInstalledPlugins()
        {
            try
            {
                return Plugin.Instance?.ApplicationHost?.Plugins?.ToList() ?? new List<IPlugin>();
            }
            catch (Exception e)
            {
                Plugin.Instance?.Logger?.Debug($"{nameof(SuppressPluginUpdate)} - list plugins failed: {e.Message}");
                return new List<IPlugin>();
            }
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
