using HarmonyLib;
using System.Reflection;

namespace StrmAssistant.Mod
{
    /// <summary>
    /// Emby 内部搜索相关方法的反射入口。
    /// 用字符串定位而不是 typeof()，这样即使类在 4.9.x/4.10 之间挪了命名空间也不会编译期断掉。
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
                var repository = assembly.GetType("Emby.Server.Implementations.Data.SqliteItemRepository");
                var baseRepository = assembly.GetType("Emby.Server.Implementations.Data.BaseSqliteRepository");

                EnableJoinFtsSearch = AccessTools.Method(repository, "EnableJoinFtsSearch");
                GetJoinCommandText = AccessTools.Method(repository, "GetJoinCommandText");
                CreateSearchTerm = AccessTools.Method(repository, "CreateSearchTerm");
                CacheIdsFromTextParams = AccessTools.Method(repository, "CacheIdsFromTextParams");
                CreateConnection = AccessTools.Method(baseRepository, "CreateConnection");
            }
            catch (System.Exception e)
            {
                Plugin.Instance?.Logger?.Warn($"{nameof(EmbySqliteApi)} Init Failed: {e.Message}");
            }
        }
    }
}
