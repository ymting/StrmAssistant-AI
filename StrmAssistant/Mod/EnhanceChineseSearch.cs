using Emby.Sqlite;
using HarmonyLib;
using MediaBrowser.Controller.Entities;
using SQLitePCL.pretty;
using StrmAssistant.Options;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace StrmAssistant.Mod
{
    /// <summary>
    /// 中文搜索增强（移植自完整社区版）。
    ///
    /// 原理：Emby 的 FTS5 索引默认用 unicode61 分词，中文只能整句匹配。
    /// 这里把自定义 SQLite 扩展 libsimple.so 加载进 Emby 的数据库连接，
    /// 提供 simple 分词器（中文分词 + 拼音），再把 fts_search9 的 tokenizer 切成 simple 并重建索引。
    ///
    /// 关键安全约束（本分支新增）：
    /// 1. 只有在 libsimple.so 能成功释放到磁盘时才允许启用；
    /// 2. 关闭时必须走 Restore 流程把 tokenizer 换回 unicode61 再重建，
    ///    否则会留下「表指向不存在的 tokenizer」的坏状态（搜索直接失效）；
    /// 3. 任何一步失败都回滚选项，不留在半开状态。
    /// </summary>
    internal class EnhanceChineseSearch : PatchBase<EnhanceChineseSearch>
    {
        private static readonly string TokenizerPath =
            Path.Combine(Plugin.Instance.ApplicationPaths.PluginsPath, "libsimple.so");

        private const string StockTokenizerName = "unicode61 remove_diacritics 2";
        private const string SimpleTokenizerName = "simple";

        // Emby 4.8.3 起搜索索引表固定为 fts_search9（4.9.1.90 实测仍然如此）
        private static readonly string FtsTableName =
            Utility.AppVer >= Utility.Ver4830 ? "fts_search9" : "fts_search8";

        private static readonly string TokenizerCheckQuery = $@"
                SELECT
                    CASE
                        WHEN instr(sql, 'tokenize=""{SimpleTokenizerName}""') > 0 THEN '{SimpleTokenizerName}'
                        WHEN instr(sql, 'tokenize=""{StockTokenizerName}""') > 0 THEN '{StockTokenizerName}'
                        ELSE 'unknown'
                    END AS tokenizer_name
                FROM
                    sqlite_master
                WHERE
                    type = 'table' AND
                    name = '{FtsTableName}';";

        public static string CurrentTokenizerName { get; private set; } = "unknown";

        private static MethodInfo _createConnection;
        private static readonly object Lock = new object();
        private static bool _patchPhase2Initialized;

        private static readonly Dictionary<string, Regex> ProviderPatterns = new Dictionary<string, Regex>
        {
            { "imdb", new Regex("^tt\\d{7,8}$", RegexOptions.IgnoreCase | RegexOptions.Compiled) },
            { "tmdb", new Regex("^tmdb(id)?=(\\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled) },
            { "tvdb", new Regex("^tvdb(id)?=(\\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled) }
        };

        public EnhanceChineseSearch()
        {
            Initialize();

            var options = GetOptions();

            if (options != null && (options.EnhanceChineseSearch || options.EnhanceChineseSearchRestore))
            {
                if (Utility.AppVer >= Utility.Ver4830)
                {
                    PatchPhase1();
                }
                else
                {
                    ResetOptions();
                }
            }
        }

        private static SearchEnhanceOptions GetOptions()
        {
            try
            {
                return Plugin.Instance?.GetPluginOptions()?.SearchEnhanceOptions;
            }
            catch
            {
                return null;
            }
        }

        protected override void OnInitialize()
        {
            // 把 sqlite3_enable_load_extension 复制成 stub，用来绕过 Emby 默认禁用扩展加载
            var targetMethod = AccessTools.Method(AccessTools.TypeByName("SQLitePCLEx.raw"),
                "sqlite3_enable_load_extension");

            PatchManager.ReversePatch(PatchTracker, targetMethod, nameof(EnableLoadExtensionStub));

            _createConnection = EmbySqliteApi.CreateConnection;
        }

        protected override void Prepare(bool apply)
        {
            // 由 PatchPhase1/PatchPhase2 直接控制，不通过通用 Patch()/Unpatch()
        }

        /// <summary>设置保存时调用：关闭开关则标记下次启动执行恢复。</summary>
        public static void MarkRestoreRequired()
        {
            var options = GetOptions();
            if (options is null) return;

            options.EnhanceChineseSearch = false;
            options.EnhanceChineseSearchRestore = true;
            Plugin.Instance.SavePluginOptionsSuppress();

            Plugin.Instance.Logger.Warn(
                "EnhanceChineseSearch - disabled. Restart Emby to restore the official tokenizer " +
                "and rebuild the search index.");
        }

        private static void PatchPhase1()
        {
            if (!EnsureTokenizerExists() ||
                !PatchManager.PatchUnpatch(Instance.PatchTracker, true, _createConnection, null,
                    nameof(CreateConnectionPostfix)))
            {
                if (Plugin.Instance.DebugMode)
                {
                    Plugin.Instance.Logger.Debug("EnhanceChineseSearch - PatchPhase1 Failed");
                }

                ResetOptions();
            }
        }

        private static void PatchPhase2(IDatabaseConnection connection)
        {
            var restoreOk = true;
            var patchOk = false;

            try
            {
                // IStatement 只实现了非泛型 IEnumerator，这里按原版方式逐条读取
                var statement = DatabaseConnection.PrepareStatement(connection, TokenizerCheckQuery);

                try
                {
                    var enumerator = (System.Collections.IEnumerator)statement;

                    if (enumerator.MoveNext())
                    {
                        var row = enumerator.Current as IResultSet;
                        CurrentTokenizerName = row?.GetString(0) ?? "unknown";
                    }
                }
                finally
                {
                    (statement as IDisposable)?.Dispose();
                }

                Plugin.Instance.Logger.Info(
                    "EnhanceChineseSearch - Current tokenizer (before) is " + CurrentTokenizerName);

                if (!string.Equals(CurrentTokenizerName, "unknown", StringComparison.Ordinal))
                {
                    var options = GetOptions();

                    if (options?.EnhanceChineseSearchRestore == true)
                    {
                        // 恢复：把 simple 换回 unicode61 并重建索引
                        if (string.Equals(CurrentTokenizerName, SimpleTokenizerName, StringComparison.Ordinal))
                        {
                            restoreOk = RebuildFts(connection, StockTokenizerName);
                        }

                        if (restoreOk)
                        {
                            CurrentTokenizerName = StockTokenizerName;
                            Plugin.Instance.Logger.Info("EnhanceChineseSearch - Restore Success");
                        }

                        ResetOptions();
                    }
                    else if (options?.EnhanceChineseSearch == true)
                    {
                        patchOk = PatchSearchFunctions();

                        if (patchOk)
                        {
                            if (string.Equals(CurrentTokenizerName, StockTokenizerName, StringComparison.Ordinal))
                            {
                                restoreOk = RebuildFts(connection, SimpleTokenizerName);
                            }

                            if (restoreOk)
                            {
                                CurrentTokenizerName = SimpleTokenizerName;
                                Plugin.Instance.Logger.Info("EnhanceChineseSearch - Load Success");
                            }
                        }
                    }
                }

                Plugin.Instance.Logger.Info(
                    "EnhanceChineseSearch - Current tokenizer (after) is " + CurrentTokenizerName);
            }
            catch (Exception e)
            {
                if (Plugin.Instance.DebugMode)
                {
                    Plugin.Instance.Logger.Debug("EnhanceChineseSearch - PatchPhase2 Failed");
                    Plugin.Instance.Logger.Debug(e.Message);
                    Plugin.Instance.Logger.Debug(e.StackTrace);
                }
            }

            if (!patchOk || !restoreOk || string.Equals(CurrentTokenizerName, "unknown", StringComparison.Ordinal))
            {
                ResetOptions();
            }
        }

        /// <summary>重建 FTS 索引（切 tokenizer 必须重建，否则新旧分词结果混在一起）。</summary>
        private static bool RebuildFts(IDatabaseConnection connection, string tokenizerName)
        {
            var dropSql = "DROP TABLE IF EXISTS " + FtsTableName;
            var prefixSql = string.Equals(tokenizerName, SimpleTokenizerName, StringComparison.Ordinal)
                ? string.Empty
                : ", prefix='1 2 3 4'";
            var createSql =
                $"CREATE VIRTUAL TABLE IF NOT EXISTS {FtsTableName} USING FTS5 (Name, OriginalTitle, SeriesName, Album, " +
                $"tokenize=\"{tokenizerName}\"{prefixSql})";

            // 4.9 起 Album 列要回查 MediaItems 取专辑名。必须用别名 A 限定外层 AlbumId，
            // 否则子查询里的 AlbumId 会解析到内层 MediaItems 自己的列，算出来是错的
            // （Emby 自身 GetAlbumForFts 就是这么写的）
            var albumColumn = Utility.AppVer >= Utility.Ver4900
                ? "(select case when A.AlbumId is null then null else (select Name from MediaItems where Id = A.AlbumId limit 1) end)"
                : "Album";

            var fillSql =
                $"insert into {FtsTableName}(RowId, Name, OriginalTitle, SeriesName, Album) select id, " +
                $"{GetSearchColumnNormalization("Name")}, " +
                $"{GetSearchColumnNormalization("OriginalTitle")}, " +
                $"{GetSearchColumnNormalization("SeriesName")}, " +
                $"{GetSearchColumnNormalization(albumColumn)} from MediaItems A";

            DatabaseConnection.BeginTransaction(connection, (TransactionMode)1);

            try
            {
                DatabaseConnection.Execute(connection, dropSql);
                DatabaseConnection.Execute(connection, createSql);

                Plugin.Instance.Logger.Info("EnhanceChineseSearch - Filling " + FtsTableName + " Start");
                DatabaseConnection.Execute(connection, fillSql);
                DatabaseConnection.CommitTransaction(connection);
                Plugin.Instance.Logger.Info("EnhanceChineseSearch - Filling " + FtsTableName + " Complete");

                return true;
            }
            catch (Exception e)
            {
                DatabaseConnection.RollbackTransaction(connection);

                if (Plugin.Instance.DebugMode)
                {
                    Plugin.Instance.Logger.Debug("EnhanceChineseSearch - RebuildFts Failed");
                    Plugin.Instance.Logger.Debug(e.Message);
                    Plugin.Instance.Logger.Debug(e.StackTrace);
                }

                Plugin.Instance.Logger.Warn("EnhanceChineseSearch - RebuildFts Failed: " + e.Message);
            }

            return false;
        }

        private static string GetSearchColumnNormalization(string columnName)
        {
            return "replace(replace(" + columnName + ",'''',''),'.','')";
        }

        /// <summary>确保 libsimple.so 存在且哈希匹配，不匹配则重新释放。</summary>
        private static bool EnsureTokenizerExists()
        {
            var tokenizerResourceName = GetTokenizerResourceName();
            var expectedSha = GetExpectedSha1();

            if (tokenizerResourceName is null || expectedSha is null)
            {
                Plugin.Instance.Logger.Warn(
                    "EnhanceChineseSearch - Unsupported platform or architecture, cannot enable.");
                return false;
            }

            try
            {
                if (File.Exists(TokenizerPath))
                {
                    var existingSha1 = ComputeSha1(TokenizerPath);

                    if (expectedSha.ContainsValue(existingSha1))
                    {
                        var version = expectedSha.Keys.Max();

                        if (existingSha1 == expectedSha[version])
                        {
                            Plugin.Instance.Logger.Info(
                                $"EnhanceChineseSearch - Tokenizer exists with matching SHA-1 for the highest version {version}");
                            return true;
                        }
                    }

                    Plugin.Instance.Logger.Info(
                        "EnhanceChineseSearch - Tokenizer exists but SHA-1 is not recognized. Overwriting...");
                }
                else
                {
                    Plugin.Instance.Logger.Info("EnhanceChineseSearch - Tokenizer does not exist. Exporting...");
                }

                ExportTokenizer(tokenizerResourceName);
                return true;
            }
            catch (Exception e)
            {
                Plugin.Instance.Logger.Warn("EnhanceChineseSearch - EnsureTokenizerExists Failed: " + e.Message);

                if (Plugin.Instance.DebugMode)
                {
                    Plugin.Instance.Logger.Debug(e.StackTrace);
                }
            }

            return false;
        }

        private static void ExportTokenizer(string resourceName)
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
            {
                if (stream is null)
                {
                    throw new FileNotFoundException("Embedded tokenizer resource not found: " + resourceName);
                }

                using var destination = new FileStream(TokenizerPath, FileMode.Create, FileAccess.Write);
                stream.CopyTo(destination);
            }

            Plugin.Instance.Logger.Info("EnhanceChineseSearch - Exported " + resourceName + " to " + TokenizerPath);
        }

        private static string GetTokenizerResourceName()
        {
            if (!Environment.Is64BitOperatingSystem) return null;

            var prefix = typeof(Plugin).Namespace + ".Tokenizer";
            string platform = null;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                if (RuntimeInformation.OSArchitecture == Architecture.X64) platform = "win_x64";
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                switch (RuntimeInformation.OSArchitecture)
                {
                    case Architecture.X64:
                        platform = "linux_x64";
                        break;
                    case Architecture.Arm64:
                        platform = "linux_arm64";
                        break;
                }
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                platform = "osx_universal";
            }

            return platform is null ? null : $"{prefix}.{platform}.libsimple.so";
        }

        private static Dictionary<Version, string> GetExpectedSha1()
        {
            if (!Environment.Is64BitOperatingSystem) return null;

            var architecture = RuntimeInformation.OSArchitecture;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && architecture == Architecture.X64)
            {
                return new Dictionary<Version, string>
                {
                    { new Version(0, 4, 0), "a83d90af9fb88e75a1ddf2436c8b67954c761c83" },
                    { new Version(0, 5, 0), "aed57350b46b51bb7d04321b7fe8e5e60b0cdbdc" },
                    { new Version(0, 5, 2), "e99315d7005c6b04b55a5f71e45eb3aac41670ba" }
                };
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                switch (architecture)
                {
                    case Architecture.X64:
                        return new Dictionary<Version, string>
                        {
                            { new Version(0, 4, 0), "f7fb8ba0b98e358dfaa87570dc3426ee7f00e1b6" },
                            { new Version(0, 5, 0), "8e36162f96c67d77c44b36093f31ae4d297b15c0" },
                            { new Version(0, 5, 2), "b1650dd9348e7242a2908eee1f998d510564d4e7" }
                        };
                    case Architecture.Arm64:
                        return new Dictionary<Version, string>
                        {
                            { new Version(0, 5, 2), "acb77b9ea2b823a1a4d8383ce501271cfb27ba19" }
                        };
                }
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return new Dictionary<Version, string>
                {
                    { new Version(0, 5, 2), "20a299e89ecf02dd75d2835c922c53c6cca133d8" }
                };
            }

            return null;
        }

        private static string ComputeSha1(string filePath)
        {
            using var sha1 = SHA1.Create();
            using var inputStream = File.OpenRead(filePath);
            return BitConverter.ToString(sha1.ComputeHash(inputStream)).Replace("-", "").ToLowerInvariant();
        }

        private static void ResetOptions()
        {
            var options = GetOptions();
            if (options is null) return;

            options.EnhanceChineseSearch = false;
            options.EnhanceChineseSearchRestore = false;
            Plugin.Instance.SavePluginOptionsSuppress();
        }

        private static bool PatchSearchFunctions()
        {
            return PatchManager.PatchUnpatch(Instance.PatchTracker, true, EmbySqliteApi.EnableJoinFtsSearch,
                       nameof(EnableJoinFtsSearchPrefix)) &&
                   PatchManager.PatchUnpatch(Instance.PatchTracker, true, EmbySqliteApi.GetJoinCommandText, null,
                       nameof(GetJoinCommandTextPostfix)) &&
                   PatchManager.PatchUnpatch(Instance.PatchTracker, true, EmbySqliteApi.CreateSearchTerm,
                       nameof(CreateSearchTermPrefix)) &&
                   PatchManager.PatchUnpatch(Instance.PatchTracker, true, EmbySqliteApi.CacheIdsFromTextParams,
                       nameof(CacheIdsFromTextParamsPrefix));
        }

        [HarmonyReversePatch(HarmonyReversePatchType.Original)]
        private static int EnableLoadExtensionStub(object db, int onoff)
        {
            throw new NotImplementedException();
        }

        private static bool LoadTokenizerExtension(IDatabaseConnection connection)
        {
            try
            {
                // 先打开 sqlite 的扩展加载开关，再 load_extension
                EnableLoadExtensionStub(Traverse.Create(connection).Field("db").GetValue(), 1);
                DatabaseConnection.Execute(connection, "SELECT load_extension('" + TokenizerPath + "')");
                return true;
            }
            catch (Exception e)
            {
                Plugin.Instance.Logger.Warn("EnhanceChineseSearch - Load tokenizer failed: " + e.Message);

                if (Plugin.Instance.DebugMode)
                {
                    Plugin.Instance.Logger.Debug(e.StackTrace);
                }

                Instance.PatchTracker.FallbackPatchApproach = PatchApproach.None;
            }

            return false;
        }

        [HarmonyPostfix]
        private static void CreateConnectionPostfix(object __instance, bool isReadOnly,
            IDatabaseConnection __result)
        {
            if (isReadOnly || _patchPhase2Initialized) return;

            lock (Lock)
            {
                if (_patchPhase2Initialized) return;

                var dbFilePath = Traverse.Create(__instance).Property("DbFilePath").GetValue<string>();

                if (dbFilePath != null && dbFilePath.EndsWith("library.db", StringComparison.OrdinalIgnoreCase) &&
                    LoadTokenizerExtension(__result))
                {
                    _patchPhase2Initialized = true;
                    PatchPhase2(__result);
                }
            }
        }

        [HarmonyPrefix]
        private static bool EnableJoinFtsSearchPrefix(InternalItemsQuery query, ref bool __result)
        {
            __result = !string.IsNullOrEmpty(query.SearchTerm);
            return false;
        }

        [HarmonyPostfix]
        private static void GetJoinCommandTextPostfix(InternalItemsQuery query,
            List<KeyValuePair<string, string>> bindParams, string mediaItemsTableQualifier, ref string __result)
        {
            if (!string.IsNullOrEmpty(query.SearchTerm) && __result != null &&
                __result.Contains("match @SearchTerm"))
            {
                __result = GetOptions()?.ExcludeOriginalTitleFromSearch == true
                    ? __result.Replace("match @SearchTerm", "match '-OriginalTitle:' || simple_query(@SearchTerm)")
                    : __result.Replace("match @SearchTerm", "match simple_query(@SearchTerm)");
            }
        }

        [HarmonyPrefix]
        private static bool CreateSearchTermPrefix(string searchTerm, ref string __result)
        {
            var parts = searchTerm.Split('.', StringSplitOptions.RemoveEmptyEntries);

            var values = string.Concat(parts.Select((part, index) =>
                    index >= parts.Length - 1 || part.Length <= 1 ? part : part + " "))
                .Trim()
                .Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(i => i.Replace("\"", "\"\"").Replace("'", string.Empty));

            __result = string.Join(" ", values);
            return false;
        }

        [HarmonyPrefix]
        private static void CacheIdsFromTextParamsPrefix(InternalItemsQuery query, IDatabaseConnection db)
        {
            var nameStartsWith = query.NameStartsWith;

            if (!string.IsNullOrEmpty(nameStartsWith))
            {
                query.SearchTerm = nameStartsWith;
                query.NameStartsWith = null;
            }

            var searchTerm = query.SearchTerm;

            if (query.IncludeItemTypes.Length == 0 && !string.IsNullOrEmpty(searchTerm))
            {
                query.IncludeItemTypes = Utility.GetSearchScope();
            }

            if (!string.IsNullOrEmpty(searchTerm))
            {
                foreach (var providerPattern in ProviderPatterns)
                {
                    var match = providerPattern.Value.Match(searchTerm.Trim());

                    if (!match.Success) continue;

                    var value = providerPattern.Key == "imdb" ? match.Value : match.Groups[2].Value;
                    query.HasAnyProviderId = new[] { providerPattern.Key };
                    query.AnyProviderIdEquals = new List<KeyValuePair<string, string>>
                    {
                        new KeyValuePair<string, string>(providerPattern.Key, value)
                    };
                    query.SearchTerm = null;
                    break;
                }
            }

            if (Utility.AppVer >= Utility.Ver49037 &&
                (!string.IsNullOrEmpty(query.SearchTerm) || query.Genres.Length != 0))
            {
                LoadTokenizerExtension(db);
            }
        }
    }
}
