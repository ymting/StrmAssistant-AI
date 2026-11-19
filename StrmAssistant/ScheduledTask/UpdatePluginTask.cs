using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Model.Activity;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Tasks;
using StrmAssistant.Properties;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.ScheduledTask
{
    public class UpdatePluginTask : IScheduledTask
    {
        private readonly ILogger _logger;
        private readonly IApplicationHost _applicationHost;
        private readonly IApplicationPaths _applicationPaths;
        private readonly IHttpClient _httpClient;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly IActivityManager _activityManager;
        private readonly ILocalizationManager _localizationManager;
        private readonly IServerApplicationHost _serverApplicationHost;

        public UpdatePluginTask(IApplicationHost applicationHost, IApplicationPaths applicationPaths,
            IHttpClient httpClient, IJsonSerializer jsonSerializer, IActivityManager activityManager,
            ILocalizationManager localizationManager, IServerApplicationHost serverApplicationHost)
        {
            _logger = Plugin.Instance.Logger;
            _applicationHost = applicationHost;
            _applicationPaths = applicationPaths;
            _httpClient = httpClient;
            _jsonSerializer = jsonSerializer;
            _activityManager = activityManager;
            _localizationManager = localizationManager;
            _serverApplicationHost = serverApplicationHost;
        }

        private static string PluginAssemblyFilename => Assembly.GetExecutingAssembly().GetName().Name + ".dll";

        // 注意：这里指向本仓库（适配 4.9+ 的修改版）。若仍指向上游 sjtuross/StrmAssistant，
        // 该任务会把上游那份只兼容到 4.8.11 的 dll 下载回来并覆盖当前插件，等于把兼容性修复抹掉。
        private static string RepoReleaseUrl =>
            "https://api.github.com/repos/ymting/StrmAssistant-AI/releases/latest";

        public string Key => "UpdatePluginTask";

        public string Name => "Update Plugin";
        //public string Name =>
        //    Resources.ResourceManager.GetString("UpdatePluginTask_Name_Update_Plugin",
        //        Plugin.Instance.DefaultUICulture);

        public string Description => Resources.ResourceManager.GetString(
            "UpdatePluginTask_Description_Updates_plugin_to_the_latest_version", Plugin.Instance.DefaultUICulture);

        public string Category => Resources.ResourceManager.GetString("PluginOptions_EditorTitle_Strm_Assistant",
            Plugin.Instance.DefaultUICulture);

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            yield return new TaskTriggerInfo
            {
                Type = TaskTriggerInfo.TriggerWeekly,
                DayOfWeek = (DayOfWeek)new Random().Next(7),
                TimeOfDayTicks = TimeSpan.FromMinutes(new Random().Next(24 * 4) * 15).Ticks
            };
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            await Task.Yield();
            progress.Report(0);

            // 「阻止插件自动更新」名单里如果写了本插件，这里也要遵守，
            // 否则用户屏蔽了自己却没效果，还会被覆盖成远端版本
            if (Mod.SuppressPluginUpdate.IsSelfSuppressed())
            {
                _logger.Info("Plugin is listed in SuppressPluginUpdates, skip update check");
                progress.Report(100);
                return;
            }

            try
            {
                var githubToken= Plugin.Instance.GetPluginOptions().AboutOptions.GitHubToken;

                using var response = await _httpClient.SendAsync(new HttpRequestOptions
                {
                    Url = RepoReleaseUrl,
                    CancellationToken = cancellationToken,
                    AcceptHeader = "application/json",
                    UserAgent = Plugin.Instance.UserAgent,
                    EnableDefaultUserAgent = false,
                    RequestHeaders =
                    {
                        ["Authorization"] = !string.IsNullOrWhiteSpace(githubToken)
                            ? $"token {githubToken}"
                            : null
                    }
                }, "GET").ConfigureAwait(false);

                await using var contentStream = response.Content;

                var apiResult = _jsonSerializer.DeserializeFromStream<ApiResponseInfo>(contentStream);

                var currentVersion = ParseVersion(Plugin.Instance.CurrentVersion);
                var remoteVersion = ParseVersion(apiResult?.tag_name);

                if (apiResult?.tag_name is null)
                {
                    _logger.Info("No release found for this fork, skip update check");
                    progress.Report(100);
                    return;
                }

                if (currentVersion.CompareTo(remoteVersion) < 0)
                {
                    _logger.Info("Found new plugin version: {0}", remoteVersion);

                    var url = (apiResult?.assets ?? new List<ApiAssetInfo>())
                        .FirstOrDefault(asset => asset.name == PluginAssemblyFilename)
                        ?.browser_download_url;
                    if (!Uri.IsWellFormedUriString(url, UriKind.Absolute)) throw new Exception("Invalid download url");

                    var githubProxy = Plugin.Instance.GetPluginOptions().AboutOptions.GitHubProxy;

                    await using (var responseStream = await _httpClient.Get(new HttpRequestOptions
                                     {
                                         Url = string.IsNullOrWhiteSpace(githubProxy)
                                             ? url
                                             : $"{githubProxy.TrimEnd('/')}/{url.TrimStart('/')}",
                                         CancellationToken = cancellationToken,
                                         UserAgent = Plugin.Instance.UserAgent,
                                         EnableDefaultUserAgent = false,
                                         Progress = progress,
                                         RequestHeaders =
                                         {
                                             ["Authorization"] = !string.IsNullOrWhiteSpace(githubToken)
                                                 ? $"token {githubToken}"
                                                 : null
                                         }
                                     })
                                     .ConfigureAwait(false))
                    {
                        using (var memoryStream = new MemoryStream())
                        {
                            await responseStream.CopyToAsync(memoryStream, 81920, cancellationToken)
                                .ConfigureAwait(false);

                            memoryStream.Seek(0, SeekOrigin.Begin);
                            var dllFilePath = Path.Combine(_applicationPaths.PluginsPath, PluginAssemblyFilename);

                            await using (var fileStream =
                                         new FileStream(dllFilePath, FileMode.Create, FileAccess.Write))
                            {
                                await memoryStream.CopyToAsync(fileStream, 81920, cancellationToken)
                                    .ConfigureAwait(false);
                            }
                        }
                    }

                    _logger.Info("Plugin update complete");

                    _activityManager.Create(new ActivityLogEntry
                    {
                        Name = string.Format(_localizationManager.GetLocalizedString("XUpdatedOnTo"), Category,
                            remoteVersion, _serverApplicationHost.FriendlyName),
                        Type = "PluginUpdateInstalled",
                        Severity = LogSeverity.Info
                    });

                    _applicationHost.NotifyPendingRestart();
                }
                else
                {
                    _ = Plugin.NotificationApi.SendMessageToAdmins(
                        $"[{Resources.PluginOptions_EditorTitle_Strm_Assistant}] {Resources.No_Update_Message}", 1000);
                    _logger.Info("No need to update");
                }
            }
            catch (Exception e)
            {
                // 本仓库可能还没有发布任何 Release，此时 404 属正常情况，不必当作更新失败告警
                if (e.Message.IndexOf("NotFound", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    e.Message.Contains("404"))
                {
                    _logger.Info("No release found for this fork, skip update check");
                    progress.Report(100);
                    return;
                }

                _activityManager.Create(new ActivityLogEntry
                {
                    Name = string.Format(_localizationManager.GetLocalizedString("NameInstallFailedOn"), Category,
                        _serverApplicationHost.FriendlyName),
                    Type = "PluginUpdateFailed",
                    Overview = e.Message,
                    Severity = LogSeverity.Error
                });

                _ = Plugin.NotificationApi.SendMessageToAdmins(
                    $"[{Resources.PluginOptions_EditorTitle_Strm_Assistant}] {Resources.Update_Failed_Message}", 1000);
                _logger.Error("Update failed: {0}", e.Message);
                _logger.Debug(e.StackTrace);
            }

            progress.Report(100);
        }

        private static Version ParseVersion(string v)
        {
            return new Version(v.StartsWith("v") ? v.Substring(1) : v);
        }

        internal class ApiResponseInfo
        {
            public string tag_name { get; set; }

            public List<ApiAssetInfo> assets { get; set; }
        }

        internal class ApiAssetInfo
        {
            public string name { get; set; }

            public string browser_download_url { get; set; }
        }
    }
}
