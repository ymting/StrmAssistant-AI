using Emby.Media.Common.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Querying;
using StrmAssistant.Options;
using StrmAssistant.Properties;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static StrmAssistant.Common.LanguageUtility;
using static StrmAssistant.Options.GeneralOptions;
using static StrmAssistant.Options.MediaInfoExtractOptions;
using static StrmAssistant.Options.MetadataEnhanceOptions;
using static StrmAssistant.Options.Utility;
using CollectionExtensions = System.Collections.Generic.CollectionExtensions;

namespace StrmAssistant.Common
{
    public class LibraryApi
    {
        private readonly ILogger _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IProviderManager _providerManager;
        private readonly IFileSystem _fileSystem;
        private readonly IMediaMountManager _mediaMountManager;
        private readonly IUserManager _userManager;

        public static ExtraType[] IncludeExtraTypes =
        {
            ExtraType.AdditionalPart, ExtraType.BehindTheScenes, ExtraType.Clip, ExtraType.DeletedScene,
            ExtraType.Interview, ExtraType.Sample, ExtraType.Scene, ExtraType.ThemeSong, ExtraType.ThemeVideo,
            ExtraType.Trailer
        };

        public static MediaContainers[] ExcludeMediaContainers
        {
            get
            {
                return Plugin.Instance.GetPluginOptions()
                    .MediaInfoExtractOptions.ImageCaptureExcludeMediaContainers
                    .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(c =>
                        Enum.TryParse<MediaContainers>(c.Trim(), true, out var container)
                            ? container
                            : (MediaContainers?)null)
                    .Where(container => container.HasValue)
                    .Select(container => container.Value)
                    .Concat(new[] { MediaContainers.Iso })
                    .Distinct()
                    .ToArray();
            }
        }

        public static string[] ExcludeMediaExtensions
        {
            get
            {
                return Plugin.Instance.GetPluginOptions()
                    .MediaInfoExtractOptions.ImageCaptureExcludeMediaContainers
                    .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .SelectMany(c =>
                    {
                        if (Enum.TryParse<MediaContainers>(c.Trim(), true, out var container))
                        {
                            var aliases = container.GetAliases();
                            return aliases?.Where(a => !string.IsNullOrWhiteSpace(a)) ?? Array.Empty<string>();
                        }

                        return Array.Empty<string>();
                    })
                    .Concat(MediaContainers.Iso.GetAliases())
                    .Where(alias => !string.IsNullOrWhiteSpace(alias))
                    .Distinct()
                    .ToArray();
            }
        }

        public static readonly HashSet<string> ExcludedCollectionTypes = new HashSet<string>
        {
            CollectionType.Books.ToString(),
            CollectionType.Photos.ToString(),
            CollectionType.Games.ToString(),
            CollectionType.LiveTv.ToString(),
            CollectionType.Playlists.ToString(),
            CollectionType.BoxSets.ToString()
        };

        public static List<string> LibraryPathsInScope;
        public static Dictionary<User, bool> AllUsers = new Dictionary<User, bool>();
        public static string[] AdminOrderedViews = Array.Empty<string>();

        public LibraryApi(ILibraryManager libraryManager, IProviderManager providerManager, IFileSystem fileSystem,
            IMediaMountManager mediaMountManager, IUserManager userManager)
        {
            _logger = Plugin.Instance.Logger;
            _libraryManager = libraryManager;
            _providerManager = providerManager;
            _fileSystem = fileSystem;
            _mediaMountManager = mediaMountManager;
            _userManager = userManager;

            UpdateLibraryPathsInScope();
            FetchUsers();
        }

        public void UpdateLibraryPathsInScope()
        {
            var validLibraryIds =
                GetValidLibraryIds(Plugin.Instance.GetPluginOptions().MediaInfoExtractOptions.LibraryScope);

            var libraries = _libraryManager.GetVirtualFolders()
                .Where(f => !ExcludedCollectionTypes.Contains(f.CollectionType) &&
                            (!validLibraryIds.Any() || validLibraryIds.Contains(f.Id)))
                .ToList();

            LibraryPathsInScope = libraries.SelectMany(l => l.Locations)
                .Select(ls => ls.EndsWith(Path.DirectorySeparatorChar.ToString())
                    ? ls
                    : ls + Path.DirectorySeparatorChar)
                .ToList();
        }

        public bool IsLibraryInScope(BaseItem item)
        {
            return !string.IsNullOrEmpty(item.Path) && LibraryPathsInScope.Any(l => item.Path.StartsWith(l));
        }

        public void FetchUsers()
        {
            var userQuery = new UserQuery
            {
                IsDisabled = false,
            };
            var allUsers = _userManager.GetUserList(userQuery);

            // 通知等后台任务也会枚举 AllUsers，写入时加锁避免并发修改
            lock (AllUsers)
            {
                foreach (var user in allUsers)
                {
                    AllUsers[user] = _userManager.GetUserById(user.InternalId).Policy.IsAdministrator;
                }
            }

            FetchAdminOrderedViews();
        }

        public void FetchAdminOrderedViews()
        {
            var firstAdmin = AllUsers.Where(kvp => kvp.Value).Select(u => u.Key).OrderBy(u => u.DateCreated)
                .FirstOrDefault();
            AdminOrderedViews = firstAdmin?.Configuration.OrderedViews ?? AdminOrderedViews;
        }

        public bool HasMediaInfo(BaseItem item)
        {
            if (!item.RunTimeTicks.HasValue) return false;

            if (item.Size == 0) return false;

            return item.GetMediaStreams().Any(i => i.Type == MediaStreamType.Video || i.Type == MediaStreamType.Audio);
        }

        public bool ImageCaptureEnabled(BaseItem item, LibraryOptions libraryOptions)
        {
            var typeName = item.ExtraType == null ? item.GetType().Name : item.DisplayParent.GetType().Name;
            var typeOptions = libraryOptions.GetTypeOptions(typeName);

            return typeOptions?.ImageFetchers?.Any(f => f == "Image Capture" || f == "Embedded Images") is true;
        }

        private List<VirtualFolderInfo> GetLibrariesWithImageCapture(List<VirtualFolderInfo> libraries)
        {
            var librariesWithImageCapture = libraries.Where(l => l.LibraryOptions.TypeOptions.Any(t =>
                    (t.ImageFetchers.Contains("Image Capture") &&
                     ((l.CollectionType == CollectionType.TvShows.ToString() && t.Type == nameof(Episode)) ||
                      (l.CollectionType == CollectionType.Movies.ToString() && t.Type == nameof(Movie)) ||
                      (l.CollectionType == CollectionType.HomeVideos.ToString() && t.Type == nameof(Video)) ||
                      (l.CollectionType == CollectionType.Music.ToString() && t.Type == nameof(MusicVideo)) ||
                      (l.CollectionType == CollectionType.MusicVideos.ToString() && t.Type == nameof(MusicVideo)) ||
                      (l.CollectionType is null && (t.Type == nameof(Episode) || t.Type == nameof(Movie))))) ||
                    (t.ImageFetchers.Contains("Embedded Images") &&
                     ((l.CollectionType == CollectionType.Music.ToString() && t.Type == nameof(Audio)) ||
                      (l.CollectionType == CollectionType.AudioBooks.ToString() && t.Type == nameof(Audio))))))
                .ToList();

            return librariesWithImageCapture;
        }

        public List<BaseItem> FetchExtractQueueItems(List<BaseItem> items)
        {
            var libraryIds = Plugin.Instance.GetPluginOptions().MediaInfoExtractOptions.LibraryScope
                ?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).ToArray();

            var includeFavorites = libraryIds == null || !libraryIds.Any() || libraryIds.Contains("-1");
            var includeExtra = Plugin.Instance.GetPluginOptions().MediaInfoExtractOptions.IncludeExtra;
            var enableImageCapture = Plugin.Instance.GetPluginOptions().MediaInfoExtractOptions.EnableImageCapture;

            var resultItems = new List<BaseItem>();

            if (IsCatchupTaskSelected(CatchupTask.MediaInfo))
            {
                if (includeFavorites) resultItems = ExpandFavorites(items, true, true, enableImageCapture);

                var incomingItems = items.Where(item => item is Video || item is Audio).ToList();

                if (libraryIds == null || !libraryIds.Any())
                {
                    resultItems = resultItems.Concat(incomingItems).ToList();
                }

                if (libraryIds != null && libraryIds.Any(id => id != "-1") && LibraryPathsInScope.Any())
                {
                    var filteredItems = incomingItems
                        .Where(i => LibraryPathsInScope.Any(p => i.ContainingFolderPath.StartsWith(p)))
                        .ToList();
                    resultItems = resultItems.Concat(filteredItems).ToList();
                }
            }

            if (IsCatchupTaskSelected(CatchupTask.IntroSkip))
            {
                var episodesIntroSkip = Plugin.ChapterApi.SeasonHasIntroCredits(items.OfType<Episode>().ToList());
                resultItems = resultItems.Concat(episodesIntroSkip).ToList();
            }

            resultItems = resultItems.Where(i => i.ExtraType is null).GroupBy(i => i.InternalId).Select(g => g.First())
                .ToList();

            var unprocessedItems = FilterUnprocessed(resultItems
                .Concat(includeExtra ? resultItems.SelectMany(f => f.GetExtras(IncludeExtraTypes)) : Enumerable.Empty<BaseItem>())
                .ToList());
            var orderedItems = OrderByDescending(unprocessedItems);

            return orderedItems;
        }

        public List<BaseItem> FetchPreExtractTaskItems()
        {
            var libraryIds = Plugin.Instance.GetPluginOptions().MediaInfoExtractOptions.LibraryScope
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).ToArray();
            var libraries = _libraryManager.GetVirtualFolders()
                .Where(f => !libraryIds.Any() || libraryIds.Contains(f.Id)).ToList();
            var librariesWithImageCapture = GetLibrariesWithImageCapture(libraries);

            _logger.Info("MediaInfoExtract - LibraryScope: " + (!libraryIds.Any()
                ? "ALL"
                : string.Join(", ",
                    libraryIds.Contains("-1")
                        ? new[] { Resources.Favorites }.Concat(libraries.Select(l => l.Name))
                        : libraries.Select(l => l.Name).DefaultIfEmpty("NONE"))));

            var includeExtra = Plugin.Instance.GetPluginOptions().MediaInfoExtractOptions.IncludeExtra;
            _logger.Info("Include Extra: " + includeExtra);
            var enableImageCapture = Plugin.Instance.GetPluginOptions().MediaInfoExtractOptions.EnableImageCapture;

            var favoritesWithExtra = Array.Empty<BaseItem>();
            if (libraryIds.Contains("-1"))
            {
                var favorites = AllUsers.Select(e => e.Key)
                    .SelectMany(user => _libraryManager.GetItemList(new InternalItemsQuery
                    {
                        User = user,
                        IsFavorite = true
                    })).GroupBy(i => i.InternalId).Select(g => g.First()).ToList();

                var expanded = ExpandFavorites(favorites, false, true, enableImageCapture);

                favoritesWithExtra = expanded.Concat(includeExtra
                        ? expanded.SelectMany(f => f.GetExtras(IncludeExtraTypes))
                        : Enumerable.Empty<BaseItem>())
                    .ToArray();
            }

            var items = Array.Empty<BaseItem>();
            var extras = Array.Empty<BaseItem>();

            if (!libraryIds.Any() || libraryIds.Any(id => id != "-1"))
            {
                var itemsMediaInfoQuery = new InternalItemsQuery
                {
                    HasPath = true,
                    HasAudioStream = false,
                    MediaTypes = new[] { MediaType.Video, MediaType.Audio }
                };

                if (libraryIds.Any(id => id != "-1") && libraries.Any())
                {
                    itemsMediaInfoQuery.PathStartsWithAny = libraries.SelectMany(l => l.Locations).Select(ls =>
                        ls.EndsWith(Path.DirectorySeparatorChar.ToString())
                            ? ls
                            : ls + Path.DirectorySeparatorChar).ToArray();
                }

                var itemsMediaInfo = _libraryManager.GetItemList(itemsMediaInfoQuery);

                var itemsImageCaptureQuery = new InternalItemsQuery
                {
                    HasPath = true,
                    MediaTypes = new[] { MediaType.Video, MediaType.Audio }
                };

                if (enableImageCapture && librariesWithImageCapture.Any())
                {
                    itemsImageCaptureQuery.PathStartsWithAny =
                        librariesWithImageCapture.SelectMany(l => l.Locations).Select(ls =>
                            ls.EndsWith(Path.DirectorySeparatorChar.ToString())
                                ? ls
                                : ls + Path.DirectorySeparatorChar).ToArray();

                    var itemsImageCapture = _libraryManager.GetItemList(itemsImageCaptureQuery)
                        .Where(i => !i.HasImage(ImageType.Primary)).ToList();
                    items = itemsMediaInfo.Concat(itemsImageCapture).GroupBy(i => i.InternalId).Select(g => g.First())
                        .ToArray();
                }
                else
                {
                    items = itemsMediaInfo;
                }

                if (includeExtra)
                {
                    itemsMediaInfoQuery.ExtraTypes = IncludeExtraTypes;
                    var extrasMediaInfo = _libraryManager.GetItemList(itemsMediaInfoQuery);

                    if (enableImageCapture && librariesWithImageCapture.Any())
                    {
                        itemsImageCaptureQuery.ExtraTypes = IncludeExtraTypes;
                        var extrasImageCapture = _libraryManager.GetItemList(itemsImageCaptureQuery);
                        extras = extrasImageCapture.Concat(extrasMediaInfo).GroupBy(i => i.InternalId)
                            .Select(g => g.First()).ToArray();
                    }
                    else
                    {
                        extras = extrasMediaInfo;
                    }
                }
            }

            var combined = favoritesWithExtra.Concat(items).Concat(extras).GroupBy(i => i.InternalId)
                .Select(g => g.First()).ToList();
            var filtered = FilterUnprocessed(combined);
            var results = OrderByDescending(filtered);

            return results;
        }

        public List<BaseItem> FetchPostExtractTaskItems(bool includeAudio)
        {
            var libraryIds = Plugin.Instance.GetPluginOptions()
                .MediaInfoExtractOptions.LibraryScope.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .ToArray();
            var libraries = _libraryManager.GetVirtualFolders()
                .Where(f => !libraryIds.Any() || libraryIds.Contains(f.Id))
                .ToList();

            _logger.Info("MediaInfoExtract - LibraryScope: " + (!libraryIds.Any()
                ? "ALL"
                : string.Join(", ",
                    libraryIds.Contains("-1")
                        ? new[] { Resources.Favorites }.Concat(libraries.Select(l => l.Name))
                        : libraries.Select(l => l.Name).DefaultIfEmpty("NONE"))));

            var includeExtra = Plugin.Instance.GetPluginOptions().MediaInfoExtractOptions.IncludeExtra;
            _logger.Info("Include Extra: " + includeExtra);

            var favoritesWithExtra = new List<BaseItem>();
            if (libraryIds.Contains("-1"))
            {
                var favorites = AllUsers.Select(e => e.Key)
                    .SelectMany(user => _libraryManager.GetItemList(new InternalItemsQuery
                    {
                        User = user, IsFavorite = true
                    }))
                    .GroupBy(i => i.InternalId)
                    .Select(g => g.First())
                    .ToList();

                var expanded = ExpandFavorites(favorites, false, false, false);

                favoritesWithExtra = expanded.Concat(includeExtra
                        ? expanded.SelectMany(f => f.GetExtras(IncludeExtraTypes))
                        : Enumerable.Empty<BaseItem>())
                    .Where(HasMediaInfo)
                    .ToList();
            }

            var itemsWithExtras = new List<BaseItem>();
            if (!libraryIds.Any() || libraryIds.Any(id => id != "-1"))
            {
                var itemsQuery = new InternalItemsQuery
                {
                    HasPath = true, HasAudioStream = true,
                    MediaTypes = includeAudio ? new[] { MediaType.Video, MediaType.Audio } : new[] { MediaType.Video }
                };

                if (libraryIds.Any(id => id != "-1") && libraries.Any())
                {
                    itemsQuery.PathStartsWithAny = libraries.SelectMany(l => l.Locations)
                        .Select(ls =>
                            ls.EndsWith(Path.DirectorySeparatorChar.ToString()) ? ls : ls + Path.DirectorySeparatorChar)
                        .ToArray();
                }

                itemsWithExtras = _libraryManager.GetItemList(itemsQuery).ToList();

                if (includeExtra)
                {
                    itemsQuery.ExtraTypes = IncludeExtraTypes;
                    itemsWithExtras = _libraryManager.GetItemList(itemsQuery).Concat(itemsWithExtras).ToList();
                }
            }

            var combined = favoritesWithExtra.Concat(itemsWithExtras)
                .GroupBy(i => i.InternalId)
                .Select(g => g.First())
                .ToList();
            var results = OrderByDescending(combined);

            return results;
        }

        private static List<BaseItem> OrderByDescending(List<BaseItem> items)
        {
            var results = items.OrderBy(i => i.ExtraType is null ? 0 : 1)
                .ThenByDescending(i =>
                    i is Episode e ? GetPremiereDateOrDefault(e) :
                    i.ExtraType != null ? i.DateCreated : i.PremiereDate ?? i.DateCreated)
                .ThenByDescending(i => i.IndexNumber ?? int.MinValue)
                .ToList();
            return results;
        }

        private static DateTimeOffset GetPremiereDateOrDefault(Episode item)
        {
            if (item.PremiereDate.HasValue && item.PremiereDate.Value != DateTimeOffset.MinValue)
                return item.PremiereDate.Value;

            if (item.Series != null)
            {
                if (item.Series.PremiereDate.HasValue && item.Series.PremiereDate.Value != DateTimeOffset.MinValue)
                    return item.Series.PremiereDate.Value;

                if (item.Series.ProductionYear is int year && year > 1 && year <= 9999)
                    return new DateTimeOffset(new DateTime(year, 1, 1), TimeSpan.Zero);
            }

            return item.DateCreated;
        }

        public static bool IsPremiereDateInScope(Episode item, DateTimeOffset lookBackTime, bool includeNoPrem)
        {
            if (item.PremiereDate.HasValue && item.PremiereDate.Value != DateTimeOffset.MinValue)
                return item.PremiereDate.Value > lookBackTime;

            if (item.Series != null)
            {
                if (item.Series.PremiereDate.HasValue && item.Series.PremiereDate.Value != DateTimeOffset.MinValue)
                    return item.Series.PremiereDate.Value > lookBackTime;

                if (item.Series.ProductionYear is int year && year > 1 && year <= 9999)
                    return year == lookBackTime.Year;
            }

            return includeNoPrem;
        }

        private List<BaseItem> FilterUnprocessed(List<BaseItem> items)
        {
            var enableImageCapture = Plugin.Instance.GetPluginOptions().MediaInfoExtractOptions.EnableImageCapture;

            var results = new List<BaseItem>();

            foreach (var item in items)
            {
                if (IsExtractNeeded(item, enableImageCapture))
                {
                    results.Add(item);
                }
                else if (item is Video)
                {
                    _logger.Debug("MediaInfoExtract - Item dropped: " + item.Name + " - " + item.Path); // video without audio
                }
            }

            _logger.Info("MediaInfoExtract - Number of items: " + results.Count);

            return results;
        }

        public bool IsExtractNeeded(BaseItem item, bool enableImageCapture)
        {
            if (item.MediaContainer.HasValue && ExcludeMediaContainers.Contains(item.MediaContainer.Value))
                return false;

            if (!item.IsShortcut && item.IsFileProtocol && !string.IsNullOrEmpty(item.Path))
            {
                var fileExtension = Path.GetExtension(item.Path).TrimStart('.');
                if (ExcludeMediaExtensions.Contains(fileExtension)) return false;
            }

            if (!HasMediaInfo(item)) return true;

            if (!enableImageCapture) return false;

            if (Plugin.Instance.GetPluginOptions().MediaInfoExtractOptions.PersistMediaInfoMode ==
                PersistMediaInfoOption.Restore.ToString()) return false;

            var libraryOptions = _libraryManager.GetLibraryOptions(item);

            switch (item)
            {
                case Video _:
                    return !item.HasImage(ImageType.Primary) && ImageCaptureEnabled(item, libraryOptions);
                case Audio _:
                    return !item.HasImage(ImageType.Primary) && ImageCaptureEnabled(item, libraryOptions) &&
                           item.GetMediaStreams().Any(i => i.Type == MediaStreamType.EmbeddedImage);
                default:
                    return false;
            }
        }

        public List<BaseItem> ExpandFavorites(List<BaseItem> items, bool filterNeeded, bool? preExtract,
            bool enableImageCapture)
        {
            var itemsMultiVersions = items.SelectMany(v =>
                    _libraryManager.GetItemList(new InternalItemsQuery
                        {
                            PresentationUniqueKey = v.PresentationUniqueKey
                        }))
                .ToList();

            var videos = itemsMultiVersions.OfType<Video>().Cast<BaseItem>().ToList();

            var seriesIds = itemsMultiVersions.OfType<Series>().Select(s => s.InternalId)
                .Union(itemsMultiVersions.OfType<Episode>().Select(e => e.SeriesId)).ToArray();

            var episodes = Array.Empty<BaseItem>();
            if (seriesIds.Length > 0)
            {
                var episodesMediaInfoQuery = new InternalItemsQuery
                {
                    HasPath = true,
                    HasAudioStream = !preExtract,
                    MediaTypes = new[] { MediaType.Video },
                    Recursive = true,
                    AncestorIds = seriesIds
                };
                var episodesMediaInfo = _libraryManager.GetItemList(episodesMediaInfoQuery);

                if (enableImageCapture)
                {
                    var episodesImageCaptureQuery = new InternalItemsQuery
                    {
                        HasPath = true,
                        MediaTypes = new[] { MediaType.Video },
                        Recursive = true,
                        AncestorIds = seriesIds
                    };
                    var episodesImageCapture = _libraryManager.GetItemList(episodesImageCaptureQuery)
                        .Where(i => !i.HasImage(ImageType.Primary)).ToList();
                    episodes = episodesMediaInfo.Concat(episodesImageCapture).GroupBy(i => i.InternalId)
                        .Select(g => g.First()).ToArray();
                }
                else
                {
                    episodes = episodesMediaInfo;
                }
            }

            var combined = videos.Concat(episodes).GroupBy(i => i.InternalId).Select(g => g.First()).ToList();

            return filterNeeded ? FilterByFavorites(combined) : combined;
        }

        private List<BaseItem> FilterByFavorites(List<BaseItem> items)
        {
            var videos = AllUsers.Select(e => e.Key)
                .SelectMany(u => items.OfType<Video>().Where(i => i.IsFavoriteOrLiked(u)));

            var episodes = AllUsers.Select(e => e.Key)
                .SelectMany(u => items.OfType<Episode>()
                    .GroupBy(e => e.SeriesId)
                    .Where(g => g.Any(i => i.IsFavoriteOrLiked(u)) || g.First().Series.IsFavoriteOrLiked(u))
                    .SelectMany(g => g));

            var results = videos.Cast<BaseItem>()
                .Concat(episodes)
                .GroupBy(i => i.InternalId)
                .Select(g => g.First())
                .ToList();

            return results;
        }

        public List<User> GetUsersByFavorites(BaseItem item)
        {
            var itemsMultiVersion = _libraryManager.GetItemList(new InternalItemsQuery
            {
                PresentationUniqueKey = item.PresentationUniqueKey
            });

            var users = AllUsers.Select(e => e.Key)
                .Where(u => itemsMultiVersion.Any(i =>
                    (i is Movie || i is Series) && i.IsFavoriteOrLiked(u) || i is Episode e &&
                    (e.IsFavoriteOrLiked(u) || e.Series != null && e.Series.IsFavoriteOrLiked(u))))
                .ToList();

            return users;
        }

        public bool HasFileChanged(BaseItem item, IDirectoryService directoryService)
        {
            if (item.IsFileProtocol)
            {
                var file = directoryService.GetFile(item.Path);
                if (file != null && item.HasDateModifiedChanged(file.LastWriteTimeUtc))
                    return true;
            }

            return false;
        }

        public void UpdateDateModifiedLastSaved(BaseItem item, IDirectoryService directoryService)
        {
            if (item.IsFileProtocol)
            {
                var file = directoryService.GetFile(item.Path);
                if (file != null && file.LastWriteTimeUtc.ToUnixTimeSeconds() > 0L)
                {
                    item.DateModified = file.LastWriteTimeUtc;
                    _libraryManager.UpdateItems(new List<BaseItem> { item }, null,
                        ItemUpdateType.None, true, false, null, CancellationToken.None);
                }
            }
        }

        public async Task<bool?> OrchestrateMediaInfoProcessAsync(BaseItem taskItem, string source, CancellationToken cancellationToken)
        {
            var persistMediaInfoMode = Plugin.Instance.GetPluginOptions().MediaInfoExtractOptions.PersistMediaInfoMode;
            var persistMediaInfo = taskItem is Video && persistMediaInfoMode != PersistMediaInfoOption.None.ToString();
            var mediaInfoRestoreMode = persistMediaInfoMode == PersistMediaInfoOption.Restore.ToString();

            var filePath = taskItem.Path;
            if (taskItem.IsShortcut)
            {
                filePath = await GetStrmMountPath(filePath).ConfigureAwait(false);
            }

            if (string.IsNullOrEmpty(filePath)) return null;

            var fileExtension = Path.GetExtension(filePath).TrimStart('.');
            var extractSkip = mediaInfoRestoreMode || ExcludeMediaExtensions.Contains(fileExtension);

            var refreshOptions = Plugin.MediaInfoApi.GetMediaInfoRefreshOptions();

            var directoryService = refreshOptions.DirectoryService;

            if (Uri.TryCreate(filePath, UriKind.Absolute, out var uri) && uri.IsAbsoluteUri &&
                uri.Scheme == Uri.UriSchemeFile)
            {
                var file = directoryService.GetFile(filePath);
                if (file?.Exists != true) return null;
            }

            var collectionFolders = (BaseItem[])_libraryManager.GetCollectionFolders(taskItem);
            var libraryOptions = _libraryManager.GetLibraryOptions(taskItem);

            var dummyLibraryOptions = CopyLibraryOptions(libraryOptions);
            dummyLibraryOptions.DisabledLocalMetadataReaders = new[] { "Nfo" };
            dummyLibraryOptions.MetadataSavers = Array.Empty<string>();

            foreach (var option in dummyLibraryOptions.TypeOptions)
            {
                option.MetadataFetchers = Array.Empty<string>();
                option.ImageFetchers = Array.Empty<string>();
            }

            if (persistMediaInfo)
            {
                var deserializeResult =
                    await Plugin.MediaInfoApi
                        .DeserializeMediaInfo(taskItem, directoryService, source, mediaInfoRestoreMode)
                        .ConfigureAwait(false);

                if (deserializeResult)
                {
                    if (Plugin.SubtitleApi.HasExternalSubtitleChanged(taskItem, directoryService, true))
                    {
                        await Plugin.SubtitleApi.UpdateExternalSubtitles(taskItem, refreshOptions, false, true)
                            .ConfigureAwait(false);
                    }

                    return false;
                }
            }

            if (extractSkip) return null;

            taskItem.DateLastRefreshed = new DateTimeOffset();

            await _providerManager
                .RefreshSingleItem(taskItem, refreshOptions, collectionFolders, dummyLibraryOptions, cancellationToken)
                .ConfigureAwait(false);

            if (persistMediaInfo)
            {
                await Plugin.MediaInfoApi.SerializeMediaInfo(taskItem.InternalId, directoryService, true, source)
                    .ConfigureAwait(false);
            }

            return true;
        }

        public static LibraryOptions CopyLibraryOptions(LibraryOptions sourceOptions)
        {
            var targetOptions = new LibraryOptions
            {
                EnableArchiveMediaFiles = sourceOptions.EnableArchiveMediaFiles,
                EnablePhotos = sourceOptions.EnablePhotos,
                EnableRealtimeMonitor = sourceOptions.EnableRealtimeMonitor,
                EnableMarkerDetection = sourceOptions.EnableMarkerDetection,
                EnableMarkerDetectionDuringLibraryScan = sourceOptions.EnableMarkerDetectionDuringLibraryScan,
                IntroDetectionFingerprintLength = sourceOptions.IntroDetectionFingerprintLength,
                EnableChapterImageExtraction = sourceOptions.EnableChapterImageExtraction,
                ExtractChapterImagesDuringLibraryScan = sourceOptions.ExtractChapterImagesDuringLibraryScan,
                DownloadImagesInAdvance = sourceOptions.DownloadImagesInAdvance,
                CacheImages = sourceOptions.CacheImages,
                PathInfos =
                    sourceOptions.PathInfos?.Select(p => new MediaPathInfo
                        {
                            Path = p.Path, NetworkPath = p.NetworkPath, Username = p.Username, Password = p.Password
                        })
                        .ToArray() ?? Array.Empty<MediaPathInfo>(),
                IgnoreHiddenFiles = sourceOptions.IgnoreHiddenFiles,
                IgnoreFileExtensions =
                    sourceOptions.IgnoreFileExtensions?.Clone() as string[] ?? Array.Empty<string>(),
                SaveLocalMetadata = sourceOptions.SaveLocalMetadata,
                SaveMetadataHidden = sourceOptions.SaveMetadataHidden,
                SaveLocalThumbnailSets = sourceOptions.SaveLocalThumbnailSets,
                ImportPlaylists = sourceOptions.ImportPlaylists,
                EnableAutomaticSeriesGrouping = sourceOptions.EnableAutomaticSeriesGrouping,
                ShareEmbeddedMusicAlbumImages = sourceOptions.ShareEmbeddedMusicAlbumImages,
                EnableEmbeddedTitles = sourceOptions.EnableEmbeddedTitles,
                EnableAudioResume = sourceOptions.EnableAudioResume,
                AutoGenerateChapters = sourceOptions.AutoGenerateChapters,
                AutomaticRefreshIntervalDays = sourceOptions.AutomaticRefreshIntervalDays,
                PlaceholderMetadataRefreshIntervalDays = sourceOptions.PlaceholderMetadataRefreshIntervalDays,
                PreferredMetadataLanguage = sourceOptions.PreferredMetadataLanguage,
                PreferredImageLanguage = sourceOptions.PreferredImageLanguage,
                ContentType = sourceOptions.ContentType,
                MetadataCountryCode = sourceOptions.MetadataCountryCode,
                MetadataSavers = sourceOptions.MetadataSavers?.Clone() as string[] ?? Array.Empty<string>(),
                DisabledLocalMetadataReaders =
                    sourceOptions.DisabledLocalMetadataReaders?.Clone() as string[] ?? Array.Empty<string>(),
                LocalMetadataReaderOrder = sourceOptions.LocalMetadataReaderOrder?.Clone() as string[] ?? null,
                DisabledLyricsFetchers =
                    sourceOptions.DisabledLyricsFetchers?.Clone() as string[] ?? Array.Empty<string>(),
                SaveLyricsWithMedia = sourceOptions.SaveLyricsWithMedia,
                LyricsDownloadMaxAgeDays = sourceOptions.LyricsDownloadMaxAgeDays,
                LyricsFetcherOrder = sourceOptions.LyricsFetcherOrder?.Clone() as string[] ?? Array.Empty<string>(),
                LyricsDownloadLanguages =
                    sourceOptions.LyricsDownloadLanguages?.Clone() as string[] ?? Array.Empty<string>(),
                DisabledSubtitleFetchers =
                    sourceOptions.DisabledSubtitleFetchers?.Clone() as string[] ?? Array.Empty<string>(),
                SubtitleFetcherOrder =
                    sourceOptions.SubtitleFetcherOrder?.Clone() as string[] ?? Array.Empty<string>(),
                SkipSubtitlesIfEmbeddedSubtitlesPresent = sourceOptions.SkipSubtitlesIfEmbeddedSubtitlesPresent,
                SkipSubtitlesIfAudioTrackMatches = sourceOptions.SkipSubtitlesIfAudioTrackMatches,
                SubtitleDownloadLanguages =
                    sourceOptions.SubtitleDownloadLanguages?.Clone() as string[] ?? Array.Empty<string>(),
                SubtitleDownloadMaxAgeDays = sourceOptions.SubtitleDownloadMaxAgeDays,
                RequirePerfectSubtitleMatch = sourceOptions.RequirePerfectSubtitleMatch,
                SaveSubtitlesWithMedia = sourceOptions.SaveSubtitlesWithMedia,
                ForcedSubtitlesOnly = sourceOptions.ForcedSubtitlesOnly,
                HearingImpairedSubtitlesOnly = sourceOptions.HearingImpairedSubtitlesOnly,
                CollapseSingleItemFolders = sourceOptions.CollapseSingleItemFolders,
                EnableAdultMetadata = sourceOptions.EnableAdultMetadata,
                ImportCollections = sourceOptions.ImportCollections,
                MinCollectionItems = sourceOptions.MinCollectionItems,
                MusicFolderStructure = sourceOptions.MusicFolderStructure,
                MinResumePct = sourceOptions.MinResumePct,
                MaxResumePct = sourceOptions.MaxResumePct,
                MinResumeDurationSeconds = sourceOptions.MinResumeDurationSeconds,
                ThumbnailImagesIntervalSeconds = sourceOptions.ThumbnailImagesIntervalSeconds,
                SampleIgnoreSize = sourceOptions.SampleIgnoreSize,
                TypeOptions = sourceOptions.TypeOptions.Select(t => new TypeOptions
                    {
                        Type = t.Type,
                        MetadataFetchers = t.MetadataFetchers?.Clone() as string[] ?? Array.Empty<string>(),
                        MetadataFetcherOrder = t.MetadataFetcherOrder?.Clone() as string[] ?? Array.Empty<string>(),
                        ImageFetchers = t.ImageFetchers?.Clone() as string[] ?? Array.Empty<string>(),
                        ImageFetcherOrder = t.ImageFetcherOrder?.Clone() as string[] ?? Array.Empty<string>(),
                        ImageOptions = t.ImageOptions?.Select(i =>
                                new ImageOption { Type = i.Type, Limit = i.Limit, MinWidth = i.MinWidth })
                            .ToArray() ?? Array.Empty<ImageOption>()
                    })
                    .ToArray()
            };

            return targetOptions;
        }

        public static bool IsFileShortcut(string path)
        {
            return path != null && string.Equals(Path.GetExtension(path), ".strm", StringComparison.OrdinalIgnoreCase);
        }

        public async Task<string> GetStrmMountPath(string strmPath)
        {
            // Emby 4.9 起 IMediaMountManager.Mount 的第一参数由 ReadOnlyMemory<char> 改回 string，
            // 同时 IMediaMount.MountedPath 被 MountedPathInfo（FileSystemMetadata）取代
            using var mediaMount = await _mediaMountManager.Mount(strmPath, null, CancellationToken.None);

            return mediaMount?.MountedPathInfo?.FullName;
        }

        public BaseItem[] GetItemsByIds(long[] itemIds)
        {
            var items = _libraryManager.GetItemList(new InternalItemsQuery { ItemIds = itemIds });

            var dict = items.ToDictionary(i => i.InternalId, i => i);

            return itemIds.Select(id => CollectionExtensions.GetValueOrDefault(dict, id))
                .Where(item => item != null)
                .ToArray();
        }

        public List<CollectionFolder> GetMovieLibraries()
        {
            var libraries = _libraryManager
                .GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { nameof(CollectionFolder) } })
                .OfType<CollectionFolder>()
                .Where(l => l.CollectionType == CollectionType.Movies.ToString() || l.CollectionType is null)
                .ToList();

            return libraries;
        }

        public List<CollectionFolder> GetSeriesLibraries()
        {
            var libraries = _libraryManager
                .GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { nameof(CollectionFolder) } })
                .OfType<CollectionFolder>()
                .Where(l => l.CollectionType == CollectionType.TvShows.ToString() || l.CollectionType is null)
                .ToList();

            return libraries;
        }

        public List<Movie> FetchSplitMovieItems()
        {
            var libraries = GetMovieLibraries();

            if (!libraries.Any()) return new List<Movie>();

            _logger.Info("MergeMovie - Libraries: " + string.Join(", ", libraries.Select(l => l.Name)));

            var libraryIds = libraries.Select(l => l.InternalId).ToArray();

            var movieQuery = new InternalItemsQuery
            {
                Recursive = true,
                ParentIds = libraryIds,
                GroupByPresentationUniqueKey = true,
                IncludeItemTypes = new[] { nameof(Movie) }
            };

            var altMovies = _libraryManager.GetItemList(movieQuery).Cast<Movie>()
                .Where(m => m.GetAlternateVersionIds().Count > 0).ToList();

            return altMovies;
        }

        public void EnsureLibraryEnabledAutomaticSeriesGrouping()
        {
            var libraries = _libraryManager.GetVirtualFolders()
                .Where(f => f.CollectionType == CollectionType.TvShows.ToString() || f.CollectionType is null)
                .ToList();

            foreach (var library in libraries)
            {
                var options = library.LibraryOptions;

                if (!options.EnableAutomaticSeriesGrouping && long.TryParse(library.ItemId, out var itemId))
                {
                    options.EnableAutomaticSeriesGrouping = true;
                    CollectionFolder.SaveLibraryOptions(itemId, options);
                }
            }
        }

        /// <summary>
        /// 找出与媒体主文件同名的关联文件（字幕、nfo、图片等）。
        /// 必须用「同名 + 分隔符」精确匹配：裸 StartsWith(basename) 会把
        /// 「Movie2.srt」「Another Movie.nfo」这类属于别的媒体的文件一起删掉。
        /// </summary>
        private FileSystemMetadata[] GetRelatedPaths(string basename, string folder)
        {
            if (string.IsNullOrEmpty(basename) || string.IsNullOrEmpty(folder))
                return Array.Empty<FileSystemMetadata>();

            var extensions = new List<string>
            {
                ".nfo", ".xml", ".srt", ".vtt", ".sub", ".idx", ".txt", ".edl",
                ".bif", ".smi", ".ttml", ".ass", ".json"
            };

            extensions.AddRange(BaseItem.SupportedImageExtensions);
            return _fileSystem.GetFiles(folder, extensions.ToArray(), false, false)
                .Where(i =>
                {
                    if (string.IsNullOrEmpty(i.FullName)) return false;

                    var name = Path.GetFileNameWithoutExtension(i.FullName);
                    if (string.IsNullOrEmpty(name)) return false;

                    // 允许：完全相同 / basename.ext / basename-xxx / basename xxx / basename_xxx
                    return string.Equals(name, basename, StringComparison.OrdinalIgnoreCase) ||
                           name.StartsWith(basename + ".", StringComparison.OrdinalIgnoreCase) ||
                           name.StartsWith(basename + "-", StringComparison.OrdinalIgnoreCase) ||
                           name.StartsWith(basename + " ", StringComparison.OrdinalIgnoreCase) ||
                           name.StartsWith(basename + "_", StringComparison.OrdinalIgnoreCase);
                })
                .ToArray();
        }

        public FileSystemMetadata[] GetDeletePaths(BaseItem item)
        {
            var basename = item.FileNameWithoutExtension;
            var folder = _fileSystem.GetDirectoryName(item.Path);
            var relatedFiles = GetRelatedPaths(basename, folder);

            return new[] { new FileSystemMetadata { FullName = item.Path, IsDirectory = item.IsFolder } }
                .Concat(relatedFiles)
                .ToArray();
        }

        /// <summary>
        /// 把一个「挂载目标文件」展开成待删清单：主文件 + 同名关联文件，只保留实际存在的项。
        /// 传入的是 STRM / 软链解析出来的真实路径，不是 Emby 库里的 .strm 文件本身。
        /// </summary>
        public FileSystemMetadata[] GetRemoteDeletePaths(string path)
        {
            if (string.IsNullOrEmpty(path)) return Array.Empty<FileSystemMetadata>();

            var folder = _fileSystem.GetDirectoryName(path);
            if (string.IsNullOrEmpty(folder) || !_fileSystem.DirectoryExists(folder))
                return Array.Empty<FileSystemMetadata>();

            var basename = Path.GetFileNameWithoutExtension(path);
            var related = GetRelatedPaths(basename, folder);

            return new[] { _fileSystem.GetFileInfo(path) }
                .Concat(related)
                .Where(f => f != null && f.Exists)
                .ToArray();
        }

        /// <summary>
        /// 删除前先解析出「Emby 自己删不到、需要级联删除」的挂载目标路径。
        /// 返回字典：key = 真实目标路径；value 含义：
        ///   true  —— 本地文件（file:// 或本地绝对路径），需要深度删除；
        ///   false —— http(s) 等远程资源，只记录、不落盘删除；
        ///   null  —— 普通媒体文件，Emby 的 DeleteFileLocation 会自己删，这里不重复删。
        /// scope 为 null 时覆盖全部媒体源；不为 null 时只处理落在本次删除路径范围内的源。
        /// </summary>
        public Dictionary<string, bool?> PrepareDeepDelete(BaseItem item, string[] scope)
        {
            var mountPaths = new Dictionary<string, bool?>(StringComparer.Ordinal);
            if (item == null) return mountPaths;

            // 文件夹（剧集 / 合集 / 播放列表）要把它下面所有子项都算进来，否则只删了文件夹本身
            var items = new List<BaseItem> { item };
            if (item is Folder folderItem)
            {
                items.AddRange(folderItem.GetItemList(new InternalItemsQuery
                {
                    Recursive = true,
                    ForceOriginalFolders = item is Playlist || item is BoxSet
                }));
            }

            var single = scope == null;
            foreach (var current in items.Where(i => i is IHasMediaSources))
            {
                // 两段解析分开兜底：GetStaticMediaSources 是反射调用，失败时降级为空字典，
                // 软链 / 普通文件分支仍然可以继续处理，不至于整条目录都不删。
                List<MediaSourceInfo> sources;
                try
                {
                    sources = current.GetMediaSources(!single, false,
                            _libraryManager.GetLibraryOptions(current))
                        .Where(s => single || (scope != null && !string.IsNullOrEmpty(s?.Path) &&
                            scope.Any(p => s.Path.StartsWith(p, StringComparison.OrdinalIgnoreCase))))
                        .ToList();
                }
                catch (Exception e)
                {
                    // 单个条目取媒体源失败不能让整个删除动作崩掉，跳过它继续处理其它条目
                    _logger.Warn($"DeepDelete - GetMediaSources failed for {current.Name}: {e.Message}");
                    continue;
                }

                // GetStaticMediaSources 给出的是 STRM 内容解析后的目标路径，按 Id 与上面的媒体源对齐
                var staticPaths = new Dictionary<string, string>();
                try
                {
                    staticPaths = (Plugin.MediaInfoApi.GetStaticMediaSources(current, !single) ??
                                   new List<MediaSourceInfo>())
                        .Where(s => !string.IsNullOrEmpty(s?.Id))
                        .GroupBy(s => s.Id)
                        .ToDictionary(g => g.Key, g => g.First().Path);
                }
                catch (Exception e)
                {
                    _logger.Warn($"DeepDelete - GetStaticMediaSources failed for {current.Name}: {e.Message}");
                }

                foreach (var source in sources)
                {
                    if (source == null || string.IsNullOrEmpty(source.Path)) continue;

                    if (IsFileShortcut(source.Path))
                    {
                        // .strm 本身由 Emby 删除，这里要顺着它指向的目标继续删；
                        // source.Id 为空时不能拿去查字典（null key 会抛异常），直接跳过
                        if (string.IsNullOrEmpty(source.Id) ||
                            !staticPaths.TryGetValue(source.Id, out var target) ||
                            string.IsNullOrEmpty(target) || IsFileShortcut(target)) continue;

                        if (Path.IsPathRooted(target))
                        {
                            // 挂载目标常见形态就是本地绝对路径（Windows D:\ / Unix /mnt/...）。
                            // 优先直接用原字符串：走 Uri.LocalPath 会把路径里的 %xx / # 当作
                            // URL 编码解码，对含这些字符的真实文件名会改错路径。
                            AddMountPath(mountPaths, target, true);
                        }
                        else if (Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.IsAbsoluteUri)
                        {
                            // file:// 转成文件系统路径后按本地文件删；http(s) 等远程资源只记录、不落盘删除
                            if (uri.Scheme == Uri.UriSchemeFile)
                                AddMountPath(mountPaths, uri.LocalPath, true);
                            else
                                AddMountPath(mountPaths, target, false);
                        }
                    }
                    else if (CommonUtility.IsSymlink(source.Path))
                    {
                        var symlinkTarget = CommonUtility.GetSymlinkTarget(source.Path);
                        if (string.IsNullOrEmpty(symlinkTarget)) continue;

                        // LinkTarget 可能是相对路径，按软链所在目录补全
                        if (!Path.IsPathRooted(symlinkTarget))
                        {
                            var linkFolder = _fileSystem.GetDirectoryName(source.Path);
                            if (string.IsNullOrEmpty(linkFolder)) continue;
                            try
                            {
                                // 归一化 .. / .，避免软链目标逃逸到预期目录之外
                                symlinkTarget = Path.GetFullPath(Path.Combine(linkFolder, symlinkTarget));
                            }
                            catch
                            {
                                continue;
                            }
                        }
                        AddMountPath(mountPaths, symlinkTarget, true);
                    }
                    else
                    {
                        // 普通本地文件：Emby 的 DeleteFileLocation 已经会删，登记但不重复删
                        AddMountPath(mountPaths, source.Path, null);
                    }
                }
            }

            return mountPaths;
        }

        /// <summary>单参数重载：不限制范围，覆盖该条目的全部媒体源（用于插件自己的「删除版本」入口）。</summary>
        public Dictionary<string, bool?> PrepareDeepDelete(BaseItem item) => PrepareDeepDelete(item, null);

        private static void AddMountPath(Dictionary<string, bool?> mountPaths, string path, bool? isLocal)
        {
            if (string.IsNullOrEmpty(path) || mountPaths.ContainsKey(path)) return;
            mountPaths[path] = isLocal;
        }

        /// <summary>
        /// 真正执行级联删除：先删目标文件及其同名关联文件，再自底向上删掉因此变空的目录。
        /// 由调用方放在后台 Task 里执行，避免阻塞 Emby 的删除请求。
        /// </summary>
        public void ExecuteDeepDelete(HashSet<string> mountPaths)
        {
            if (mountPaths == null || mountPaths.Count == 0) return;

            var comparer = new CommonUtility.FileSystemMetadataComparer();
            var allPaths = new HashSet<FileSystemMetadata>(comparer);

            foreach (var mountPath in mountPaths)
            {
                foreach (var file in GetRemoteDeletePaths(mountPath))
                {
                    allPaths.Add(file);
                    var directory = _fileSystem.GetDirectoryName(file.FullName);
                    if (!string.IsNullOrEmpty(directory))
                    {
                        allPaths.Add(new FileSystemMetadata { FullName = directory, IsDirectory = true });
                    }
                }
            }

            // 一、删文件（含字幕 / nfo / 图片等关联文件）
            foreach (var file in allPaths.Where(p => !p.IsDirectory))
            {
                try
                {
                    _logger.Info("DeepDelete - Attempting to delete file: " + file.FullName);
                    _fileSystem.DeleteFile(file.FullName, true);
                }
                catch (Exception e)
                {
                    _logger.Error("DeepDelete - Failed to delete file: " + file.FullName);
                    _logger.Error(e.Message);
                    _logger.Debug(e.StackTrace);
                }
            }

            // 二、自底向上删空目录。删掉一个空目录后把它的父目录也加入队列；
            //     processed 防止「父目录被反复加回」造成死循环（社区版没有这层保护）。
            var directories = new HashSet<FileSystemMetadata>(allPaths.Where(p => p.IsDirectory), comparer);
            var processed = new HashSet<string>(StringComparer.Ordinal);

            while (directories.Count > 0)
            {
                var directory = directories.First();
                directories.Remove(directory);
                if (string.IsNullOrEmpty(directory.FullName) || !processed.Add(directory.FullName)) continue;

                try
                {
                    if (!CommonUtility.IsDirectoryEmpty(directory.FullName)) continue;

                    _logger.Info("DeepDelete - Attempting to delete empty folder: " + directory.FullName);
                    _fileSystem.DeleteDirectory(directory.FullName, true, true);

                    var parent = _fileSystem.GetDirectoryName(directory.FullName);
                    // 只向上清理到挂载目录，绝不把文件系统根（C:\ / /）纳入删除队列
                    if (!string.IsNullOrEmpty(parent) &&
                        !string.Equals(parent, directory.FullName, StringComparison.Ordinal) &&
                        !string.Equals(parent, Path.GetPathRoot(directory.FullName),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        directories.Add(new FileSystemMetadata { FullName = parent, IsDirectory = true });
                    }
                }
                catch (Exception e)
                {
                    _logger.Error("DeepDelete - Failed to delete empty folder: " + directory.FullName);
                    _logger.Error(e.Message);
                    _logger.Debug(e.StackTrace);
                }
            }
        }

        public async Task RefreshEpisodeMetadata(Episode taskItem, CancellationToken cancellationToken)
        {
            var collectionFolders = (BaseItem[])_libraryManager.GetCollectionFolders(taskItem);
            var libraryOptions = _libraryManager.GetLibraryOptions(taskItem);
            var dummyLibraryOptions = CopyLibraryOptions(libraryOptions);
            var typeOption = dummyLibraryOptions.TypeOptions.FirstOrDefault(o => o.Type == nameof(Episode));

            if (typeOption != null)
            {
                if (typeOption.MetadataFetchers.Length == 0)
                {
                    typeOption.MetadataFetchers = new[] { "TheMovieDb" };
                }

                if (typeOption.ImageFetchers.Length == 0)
                {
                    typeOption.ImageFetchers = new[] { "TheMovieDb" };
                }
            }

            var refreshOptions = Plugin.MetadataApi.GetMetadataFullRefreshOptions();

            await _providerManager
                .RefreshSingleItem(taskItem, refreshOptions, collectionFolders, dummyLibraryOptions,
                    cancellationToken).ConfigureAwait(false);
        }

        private bool EpisodeNeedsRefresh(Episode item, DateTimeOffset lookBackTime,
            HashSet<EpisodeRefreshOption> episodeRefreshOptions, bool includeNoPrem)
        {
            var name = item.Name;
            var overview = item.Overview;
            var primaryImageInfo = item.GetImageInfo(ImageType.Primary, 0);
            var dimensionsMatch = item.Width > 0 && item.Height > 0 && item.Width == primaryImageInfo?.Width &&
                                  item.Height == primaryImageInfo?.Height;
            var hasProviderIds = item.Series != null && (item.Series.HasProviderId(MetadataProviders.Tmdb) ||
                                                         item.Series.HasProviderId(MetadataProviders.Imdb) ||
                                                         item.Series.HasProviderId(MetadataProviders.Tvdb));

            var needsRefresh = string.IsNullOrWhiteSpace(overview) ||
                               (episodeRefreshOptions.Contains(EpisodeRefreshOption.NoImage) &&
                                primaryImageInfo is null) ||
                               (episodeRefreshOptions.Contains(EpisodeRefreshOption.NonChineseOverview) &&
                                !IsChinese(overview)) ||
                               (episodeRefreshOptions.Contains(EpisodeRefreshOption.DefaultEpisodeName) &&
                                IsDefaultChineseEpisodeName(name)) ||
                               (episodeRefreshOptions.Contains(EpisodeRefreshOption.ReplaceCapturedImage) &&
                                dimensionsMatch);

            return needsRefresh && hasProviderIds && IsPremiereDateInScope(item, lookBackTime, includeNoPrem);
        }

        public List<Episode> FetchEpisodeRefreshTaskItems()
        {
            var lookBackDays = Plugin.Instance.GetPluginOptions().MetadataEnhanceOptions.EpisodeRefreshLookbackDays;
            _logger.Info("EpisodeRefresh - Lookback days: " + lookBackDays);
            var lookBackTime = DateTimeOffset.UtcNow.AddDays(-lookBackDays);

            var episodeRefreshScope = Plugin.Instance.GetPluginOptions().MetadataEnhanceOptions.EpisodeRefreshScope;
            _logger.Info("EpisodeRefresh - Scope: " + episodeRefreshScope);
            var episodeRefreshOptions = Enum.GetValues(typeof(EpisodeRefreshOption))
                .Cast<EpisodeRefreshOption>()
                .Where(o => episodeRefreshScope?.Contains(o.ToString(), StringComparison.OrdinalIgnoreCase) is true)
                .ToHashSet();

            var itemsToRefresh = _libraryManager
                .GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { nameof(Episode) }, HasIndexNumber = true, IsLocked = false
                })
                .OfType<Episode>()
                .Where(e => EpisodeNeedsRefresh(e, lookBackTime, episodeRefreshOptions, true))
                .OrderByDescending(GetPremiereDateOrDefault)
                .ToList();

            _logger.Info("EpisodeRefresh - Number of items: " + itemsToRefresh.Count);

            return itemsToRefresh;
        }

        public List<Episode> FetchEpisodeRefreshQueueItems(List<Episode> items)
        {
            const int lookBackDays = 90;
            _logger.Info("EpisodeRefresh - Lookback days: " + lookBackDays);
            var lookBackTime = DateTimeOffset.UtcNow.AddDays(-lookBackDays);

            var episodeRefreshScope = Plugin.Instance.GetPluginOptions().MetadataEnhanceOptions.EpisodeRefreshScope;
            _logger.Info("EpisodeRefresh - Scope: " + episodeRefreshScope);
            var episodeRefreshOptions = Enum.GetValues(typeof(EpisodeRefreshOption))
                .Cast<EpisodeRefreshOption>()
                .Where(o => episodeRefreshScope?.Contains(o.ToString(), StringComparison.OrdinalIgnoreCase) is true)
                .ToHashSet();

            var groupedBySeason = items.GroupBy(i => i.Season);
            var itemsToRefresh = new List<Episode>();

            foreach (var group in groupedBySeason)
            {
                var season = group.Key;

                var episodes = season.GetEpisodes(new InternalItemsQuery
                    {
                        ExcludeItemIds = group.Select(e => e.InternalId).ToArray(),
                        IncludeItemTypes = new[] { nameof(Episode) },
                        HasIndexNumber = true,
                        IsLocked = false,
                        OrderBy = new (string, SortOrder)[] { (ItemSortBy.IndexNumber, SortOrder.Ascending) }
                    })
                    .Items.OfType<Episode>()
                    .Where(e => EpisodeNeedsRefresh(e, lookBackTime, episodeRefreshOptions, false));

                itemsToRefresh.AddRange(episodes);
            }

            _logger.Info("EpisodeRefresh - Number of items: " + itemsToRefresh.Count);

            return itemsToRefresh;
        }
    }
}
