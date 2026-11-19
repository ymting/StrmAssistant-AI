using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using StrmAssistant.Web.Api;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using static StrmAssistant.Common.CommonUtility;
using static StrmAssistant.Common.LanguageUtility;

namespace StrmAssistant.Web.Service
{
    public class LibraryService : BaseApiService
    {
        private readonly ILogger _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IFileSystem _fileSystem;
        private readonly IItemRepository _itemRepository;

        public LibraryService(ILibraryManager libraryManager, IItemRepository itemRepository, IFileSystem fileSystem)
        {
            _logger = Plugin.Instance.Logger;
            _libraryManager = libraryManager;
            _itemRepository = itemRepository;
            _fileSystem = fileSystem;
        }

        public void Any(DeleteVersion request)
        {
            var item = _libraryManager.GetItemById(request.Id);

            if (!(item is Video video) || !(video is Movie || video is Episode) || !video.IsFileProtocol ||
                video.GetAlternateVersionIds().Count == 0)
            {
                return;
            }
            
            var user = GetUserForRequest(null);
            var collectionFolders = _libraryManager.GetCollectionFolders(item);

            if (user is null)
            {
                if (!item.CanDelete())
                {
                    return;
                }
            }
            else if (!item.CanDelete(user, collectionFolders))
            {
                return;
            }

            // 深度删除：必须在 Emby 清掉库记录前把 STRM / 软链的真实目标解析出来，
            // 否则删完就查不到媒体源了（与 DeleteItem 补丁同样的时机要求）。
            var deepDeleteEnabled = Plugin.Instance.GetPluginOptions()?.ExperienceEnhanceOptions?.EnableDeepDelete == true;
            var allMountPaths = new Dictionary<string, bool?>();

            var deleteItems = item is Episode episode && request.DeleteParent
                ? GetSeasonEpisodesSameVersion(episode)
                : new List<BaseItem> { item };

            foreach (var deleteItem in deleteItems)
            {
                var deepDeleteState = deepDeleteEnabled
                    ? Plugin.LibraryApi.PrepareDeepDelete(deleteItem)
                    : null;

                var proceedToDelete = true;
                var deletePaths = Plugin.LibraryApi.GetDeletePaths(deleteItem);

                foreach (var path in deletePaths)
                {
                    try
                    {
                        if (!path.IsDirectory)
                        {
                            _logger.Info("DeleteVersion - Attempting to delete file: " + path.FullName);
                            _fileSystem.DeleteFile(path.FullName, true);
                        }
                    }
                    catch (Exception e)
                    {
                        if (e is IOException || e is UnauthorizedAccessException)
                        {
                            proceedToDelete = false;
                            _logger.Error("DeleteVersion - Failed to delete file: " + path.FullName);
                            _logger.Error(e.Message);
                            _logger.Debug(e.StackTrace);
                        }
                    }
                }

                if (proceedToDelete)
                {
                    _itemRepository.DeleteItems(new[] { deleteItem });

                    try
                    {
                        _fileSystem.DeleteDirectory(item.GetInternalMetadataPath(), true, true);
                    }
                    catch
                    {
                        // ignored
                    }

                    // 只有本条目确实删成功了，才把它的挂载目标纳入级联删除
                    if (deepDeleteState != null)
                    {
                        foreach (var kv in deepDeleteState)
                        {
                            allMountPaths[kv.Key] = kv.Value;
                        }
                    }
                }
            }

            if (allMountPaths.Count == 0) return;

            var localMountPaths = new HashSet<string>(
                allMountPaths.Where(kv => kv.Value ?? false).Select(kv => kv.Key));
            var notifiedPaths = new HashSet<string>(allMountPaths.Keys);
            var notifiedItem = item;

            // 明确 fire-and-forget：删除与通知都不阻塞「删除版本」请求
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
                        _logger.Warn($"DeleteVersion - DeepDelete failed: {e.Message}");
                    }
                }

                Plugin.NotificationApi?.DeepDeleteSendNotificationToAdmins(notifiedItem, notifiedPaths);
            });
        }

        private List<BaseItem> GetSeasonEpisodesSameVersion(Episode episode)
        {
            var seasonFolderChildren = episode.Parent.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { nameof(Episode) },
                    Recursive = false,
                    GroupByPresentationUniqueKey = false,
                })
                .ToList();

            var seasonEpisodesCount = episode.Season.GetEpisodeIds(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { nameof(Episode) }, GroupByPresentationUniqueKey = true
                })
                .Length;

            if (seasonFolderChildren.Count == seasonEpisodesCount)
            {
                return seasonFolderChildren;
            }

            var targetCleaned = CleanEpisodeName(episode.FileNameWithoutExtension);

            var allEpisodes = episode.Season
                .GetEpisodes(new InternalItemsQuery
                {
                    GroupByPresentationUniqueKey = false, EnableTotalRecordCount = false
                })
                .Items.ToList();

            var similarEpisodes = new List<BaseItem>();

            foreach (var ep in allEpisodes)
            {
                var cleanedName = CleanEpisodeName(ep.FileNameWithoutExtension);
                var similarity = LevenshteinDistance(targetCleaned, cleanedName);

                if (similarity > 0.92)
                {
                    similarEpisodes.Add(ep);
                }
            }

            return similarEpisodes;
        }
    }
}
