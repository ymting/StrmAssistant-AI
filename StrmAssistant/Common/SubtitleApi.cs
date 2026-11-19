using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.MediaInfo;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Common
{
    public class SubtitleApi
    {
        private readonly ILogger _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IItemRepository _itemRepository;
        private readonly IFileSystem _fileSystem;

        private readonly object _subtitleResolver;
        private readonly MethodInfo _getExternalSubtitleStreams;
        private readonly object _ffProbeSubtitleInfo;
        private readonly MethodInfo _updateExternalSubtitleStream;

        private static readonly HashSet<string> ProbeExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".sub", ".smi", ".sami", ".mpl" };

        public SubtitleApi(ILibraryManager libraryManager, IFileSystem fileSystem, IMediaProbeManager mediaProbeManager,
            ILocalizationManager localizationManager, IItemRepository itemRepository)
        {
            _logger = Plugin.Instance.Logger;
            _libraryManager = libraryManager;
            _itemRepository = itemRepository;
            _fileSystem = fileSystem;

            try
            {
                var embyProviders = Assembly.Load("Emby.Providers");
                var subtitleResolverType = embyProviders.GetType("Emby.Providers.MediaInfo.SubtitleResolver");
                var subtitleResolverConstructor = ReflectionHelper.ResolveConstructor(subtitleResolverType,
                    localizationManager, fileSystem, libraryManager);

                _subtitleResolver = subtitleResolverConstructor?.Invoke(
                    ReflectionHelper.BindArguments(subtitleResolverConstructor, null,
                        localizationManager, fileSystem, libraryManager));

                // Emby 4.9 起 SubtitleResolver.GetExternalSubtitleStreams 被
                // BaseTrackResolver.GetExternalTracks(item, startIndex, directoryService, libraryOptions,
                // namingOptions, clearCache) 取代（多了一个 libraryOptions 参数），这里优先取新方法。
                _getExternalSubtitleStreams = ReflectionHelper.ResolveMethod(subtitleResolverType,
                                                "GetExternalTracks", typeof(BaseItem)) ??
                                            ReflectionHelper.ResolveMethod(subtitleResolverType,
                                                "GetExternalSubtitleStreams", typeof(BaseItem));

                var ffProbeSubtitleInfoType = embyProviders.GetType("Emby.Providers.MediaInfo.FFProbeSubtitleInfo");
                var ffProbeSubtitleInfoConstructor = ReflectionHelper.ResolveConstructor(ffProbeSubtitleInfoType,
                    mediaProbeManager);

                _ffProbeSubtitleInfo = ffProbeSubtitleInfoConstructor?.Invoke(
                    ReflectionHelper.BindArguments(ffProbeSubtitleInfoConstructor, null, mediaProbeManager));

                _updateExternalSubtitleStream = ReflectionHelper.ResolveMethod(ffProbeSubtitleInfoType,
                    "UpdateExternalSubtitleStream", typeof(BaseItem));
            }
            catch (Exception e)
            {
                if (Plugin.Instance.DebugMode)
                {
                    _logger.Debug(e.Message);
                    _logger.Debug(e.StackTrace);
                }
            }

            if (_subtitleResolver is null || _getExternalSubtitleStreams is null ||
                _ffProbeSubtitleInfo is null || _updateExternalSubtitleStream is null)
            {
                _logger.Warn($"{nameof(SubtitleApi)} Init Failed");
            }
        }

        private List<MediaStream> GetExternalSubtitleStreams(BaseItem item, int startIndex,
            IDirectoryService directoryService, bool clearCache)
        {
            var namingOptions = _libraryManager.GetNamingOptions();
            var libraryOptions = _libraryManager.GetLibraryOptions(item);

            var args = ReflectionHelper.BindArguments(_getExternalSubtitleStreams,
                new Dictionary<string, object>
                {
                    { "item", item },
                    { "startIndex", startIndex },
                    { "directoryService", directoryService },
                    { "libraryOptions", libraryOptions },
                    { "namingOptions", namingOptions },
                    { "clearCache", clearCache }
                },
                item, directoryService, libraryOptions, namingOptions);

            return (List<MediaStream>)_getExternalSubtitleStreams.Invoke(_subtitleResolver, args);
        }

        private Task<bool> UpdateExternalSubtitleStream(BaseItem item,
            MediaStream subtitleStream, MetadataRefreshOptions options, CancellationToken cancellationToken)
        {
            var libraryOptions = _libraryManager.GetLibraryOptions(item);

            var args = ReflectionHelper.BindArguments(_updateExternalSubtitleStream,
                new Dictionary<string, object>
                {
                    { "item", item },
                    { "stream", subtitleStream },
                    { "options", options },
                    { "libraryOptions", libraryOptions },
                    { "cancellationToken", cancellationToken }
                },
                item, subtitleStream, options, libraryOptions);

            return (Task<bool>)_updateExternalSubtitleStream.Invoke(_ffProbeSubtitleInfo, args);
        }

        public MetadataRefreshOptions GetExternalSubtitleRefreshOptions()
        {
            return new MetadataRefreshOptions(new DirectoryService(_logger, _fileSystem))
            {
                EnableRemoteContentProbe = true,
                MetadataRefreshMode = MetadataRefreshMode.ValidationOnly,
                ReplaceAllMetadata = false,
                ImageRefreshMode = MetadataRefreshMode.ValidationOnly,
                ReplaceAllImages = false,
                EnableThumbnailImageExtraction = false,
                EnableSubtitleDownloading = false
            };
        }

        public bool HasExternalSubtitleChanged(BaseItem item, IDirectoryService directoryService, bool clearCache)
        {
            // Emby 4.9 移除了 ILibraryManager.GetExternalSubtitleFiles(long)，
            // 改由 IItemRepository.GetExternalTracks 直接读取库中已记录的外挂轨道（Tuple<路径, 类型>）。
            var currentExternalSubtitleFiles = _itemRepository
                .GetExternalTracks(item.InternalId, new[] { MediaStreamType.Subtitle }, CancellationToken.None)
                .Select(t => t.Item1)
                .ToList();
            var currentSet = new HashSet<string>(currentExternalSubtitleFiles, StringComparer.Ordinal);

            try
            {
                var newExternalSubtitleFiles = GetExternalSubtitleStreams(item, 0, directoryService, clearCache)
                    .Select(i => i.Path)
                    .ToArray();
                var newSet = new HashSet<string>(newExternalSubtitleFiles, StringComparer.Ordinal);

                return !currentSet.SetEquals(newSet);
            }
            catch
            {
                // ignored
            }

            return false;
        }

        public async Task UpdateExternalSubtitles(BaseItem item, MetadataRefreshOptions refreshOptions, bool clearCache,
            bool persistMediaInfo)
        {
            var directoryService = refreshOptions.DirectoryService;
            var currentStreams = item.GetMediaStreams()
                .FindAll(i =>
                    !(i.IsExternal && i.Type == MediaStreamType.Subtitle && i.Protocol == MediaProtocol.File));
            var startIndex = currentStreams.Count == 0 ? 0 : currentStreams.Max(i => i.Index) + 1;

            if (GetExternalSubtitleStreams(item, startIndex, directoryService, clearCache) is
                { } externalSubtitleStreams)
            {
                foreach (var subtitleStream in externalSubtitleStreams)
                {
                    var extension = Path.GetExtension(subtitleStream.Path);
                    if (!string.IsNullOrEmpty(extension) && ProbeExtensions.Contains(extension))
                    {
                        var result =
                            await UpdateExternalSubtitleStream(item, subtitleStream, refreshOptions,
                                CancellationToken.None).ConfigureAwait(false);

                        if (!result)
                            _logger.Warn("No result when probing external subtitle file: {0}", subtitleStream.Path);
                    }

                    _logger.Info("ExternalSubtitle - Subtitle Processed: " + subtitleStream.Path);
                }

                currentStreams.AddRange(externalSubtitleStreams);
                _itemRepository.SaveMediaStreams(item.InternalId, currentStreams, CancellationToken.None);

                if (persistMediaInfo && Plugin.LibraryApi.IsLibraryInScope(item))
                {
                    _ = Plugin.MediaInfoApi.SerializeMediaInfo(item.InternalId, directoryService, true,
                        "External Subtitle Update").ConfigureAwait(false);
                }
            }
        }
    }
}
