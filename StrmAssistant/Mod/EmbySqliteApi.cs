using HarmonyLib;
using System;
using System.Linq;
using System.Reflection;

namespace StrmAssistant.Mod
{
    /// <summary>
    /// Emby 内部搜索相关方法的反射入口。
    /// 用字符串定位而不是 typeof()，这样即使类在 4.9.x/4.10 之间挪了命名空间也不会编译期断掉。
    ///
    /// 坑：GetJoinCommandText 有两个重载（3 参 / 5 参）。直接用 AccessTools.Method(type, name)
    /// 会因歧义抛 AmbiguousMatchException，让整个静态构造失败、四个 MethodInfo 全部为 null，
    /// 中文搜索增强因此静默失效（只有一条被吞掉的 Warn）。必须像完整社区版那样
    /// 按参数数量明确挑参数最多的那个（实际被调用的是 5 参版）。
    /// </summary>
    internal static class EmbySqliteApi
    {
        internal static readonly MethodInfo EnableJoinFtsSearch;
        internal static readonly MethodInfo GetJoinCommandText;
        internal static readonly MethodInfo CreateSearchTerm;
        internal static readonly MethodInfo CacheIdsFromTextParams;
        internal static readonly MethodInfo CreateConnection;

        static EmbySqliteApi()
        {
            try
            {
                var assembly = Assembly.Load("Emby.Server.Implementations");
                var repository = assembly?.GetType("Emby.Server.Implementations.Data.SqliteItemRepository");

                // BaseSqliteRepository 不在 Emby.Server.Implementations.dll，而在 Emby.Sqlite.dll，
                // 且运行时命名空间是 Emby.Sqlite（非元数据里看到的 Emby.Server.Implementations.Data）。
                // 完整社区版用 typeof(BaseSqliteRepository) 编译期引用，天然正确；
                // 本 fork 改成字符串反射后曾从错误的程序集 + 命名空间找，导致 CreateConnection 一直为 null。
                // 实测（4.9.1.90）：Emby.Sqlite.BaseSqliteRepository，CreateConnection 定义在此类型上。
                var sqliteAssembly = Assembly.Load("Emby.Sqlite");
                var baseRepository = sqliteAssembly?.GetType("Emby.Sqlite.BaseSqliteRepository");

                if (repository is null || baseRepository is null)
                {
                    Plugin.Instance?.Logger?.Warn(
                        $"{nameof(EmbySqliteApi)} - SqliteItemRepository / BaseSqliteRepository not found " +
                        $"(repo={repository != null}, base={baseRepository != null})");
                    return;
                }

                EnableJoinFtsSearch = PickMethod(repository, "EnableJoinFtsSearch");
                // 两个重载，必须显式挑参数最多的 5 参版；裸 AccessTools.Method 会歧义抛异常
                GetJoinCommandText = PickMethod(repository, "GetJoinCommandText");
                CreateSearchTerm = PickMethod(repository, "CreateSearchTerm");
                CacheIdsFromTextParams = PickMethod(repository, "CacheIdsFromTextParams");
                // CreateConnection 有 0/1/2 参三个重载，postfix 签名是 (instance, bool isReadOnly, result)，
                // 必须挑 1 参（bool isReadOnly）那个；挑 0 参则注入不到 isReadOnly，挑 2 参多余一个 token。
                CreateConnection = PickMethod(baseRepository, "CreateConnection", 1);

                if (GetJoinCommandText is null)
                {
                    Plugin.Instance?.Logger?.Warn($"{nameof(EmbySqliteApi)} - GetJoinCommandText not found");
                }
            }
            catch (Exception e)
            {
                Plugin.Instance?.Logger?.Warn($"{nameof(EmbySqliteApi)} Init Failed: {e.Message}");
            }
        }

        /// <summary>
        /// 按方法名取声明方法，避开 AccessTools.Method 遇到多个重载时的歧义异常。
        /// 默认取参数最多的重载；expectedParamCount 非 null 时精确取对应参数数的那个。
        /// </summary>
        private static MethodInfo PickMethod(Type type, string name, int? expectedParamCount = null)
        {
            try
            {
                var candidates = type
                    .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                                BindingFlags.Static | BindingFlags.DeclaredOnly)
                    .Where(m => m.Name == name);

                return expectedParamCount.HasValue
                    ? candidates.FirstOrDefault(m => m.GetParameters().Length == expectedParamCount.Value)
                    : candidates.OrderByDescending(m => m.GetParameters().Length).FirstOrDefault();
            }
            catch (Exception e)
            {
                Plugin.Instance?.Logger?.Warn($"{nameof(EmbySqliteApi)} - pick {name} failed: {e.Message}");
                return null;
            }
        }
    }
}
