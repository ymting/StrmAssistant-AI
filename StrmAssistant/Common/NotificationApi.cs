using Emby.Notifications;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Notifications;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Session;
using StrmAssistant.Properties;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Common
{
    public class NotificationApi
    {
        private readonly ILogger _logger;
        private readonly INotificationManager _notificationManager;
        private readonly IUserManager _userManager;
        private readonly ISessionManager _sessionManager;

        public NotificationApi(INotificationManager notificationManager, IUserManager userManager, ISessionManager sessionManager)
        {
            _logger = Plugin.Instance.Logger;
            _notificationManager = notificationManager;
            _userManager = userManager;
            _sessionManager = sessionManager;
        }

        public void FavoritesUpdateSendNotification(BaseItem item)
        {
            Resources.Culture = Thread.CurrentThread.CurrentUICulture;

            var users = Plugin.LibraryApi.GetUsersByFavorites(item);
            foreach (var user in users)
            {
                var request = new NotificationRequest
                {
                    Title = Resources.PluginOptions_EditorTitle_Strm_Assistant,
                    EventId = "favorites.update",
                    User = user,
                    Item = item,
                    Description =
                        string.Format(
                            Resources.Notification_CatchupUpdate_EventDescription.Replace("\\n",
                                Environment.NewLine), item.Path, user)
                };
                _notificationManager.SendNotification(request);
            }
        }

        public void DeepDeleteSendNotification(BaseItem item, User user, HashSet<string> mountPaths)
        {
            Resources.Culture = Thread.CurrentThread.CurrentUICulture;

            var mountPathList = string.Join(Environment.NewLine, mountPaths);

            var request = new NotificationRequest
            {
                Title = Resources.PluginOptions_EditorTitle_Strm_Assistant + " - " +
                        Resources.Notification_DeepDelete_EventName,
                EventId = "deep.delete",
                User = user,
                Item = item,
                Description =
                    string.Format(
                        Resources.Notification_DeepDelete_EventDescription.Replace("\\n", Environment.NewLine),
                        item?.Name ?? string.Empty, item?.Path ?? string.Empty, mountPathList)
            };

            _notificationManager.SendNotification(request);
        }

        /// <summary>
        /// 深度删除完成后广播给所有管理员。
        /// 完整社区版是发给「发起删除的那一个用户」（由增强通知系统用 AsyncLocal 传递），
        /// Lite 没有那套请求级用户上下文，这里退化为管理员广播，两个删除入口共用。
        /// </summary>
        public void DeepDeleteSendNotificationToAdmins(BaseItem item, HashSet<string> mountPaths)
        {
            if (item is null) return;

            try
            {
                // FetchUsers 写入时也会 lock 同一个对象，这里先取快照再逐个发，
                // 避免后台线程枚举期间集合被修改（InvalidOperationException）
                var allUsers = LibraryApi.AllUsers;
                if (allUsers is null) return;

                User[] admins;
                lock (allUsers)
                {
                    admins = allUsers.Where(kvp => kvp.Value).Select(kvp => kvp.Key).ToArray();
                }

                foreach (var admin in admins)
                {
                    DeepDeleteSendNotification(item, admin, mountPaths);
                }
            }
            catch (Exception e)
            {
                // 通知失败不能影响删除结果
                _logger.Warn($"DeepDelete - Notify admins failed: {e.Message}");
            }
        }

        public async Task IntroUpdateSendNotification(Episode episode, SessionInfo session, string introStartTime,
            string introEndTime)
        {
            Resources.Culture = Thread.CurrentThread.CurrentUICulture;

            if (CanDisplayMessage(session))
            {
                var message = new MessageCommand
                {
                    Header = Resources.PluginOptions_EditorTitle_Strm_Assistant,
                    Text = string.Format(
                        Resources.Notification_IntroUpdate_Message, episode.FindSeriesName(), episode.FindSeasonName()),
                    TimeoutMs = 500
                };
                await _sessionManager.SendMessageCommand(session.Id, session.Id, message, CancellationToken.None);
            }

            var request = new NotificationRequest
            {
                Title =
                    Resources.PluginOptions_EditorTitle_Strm_Assistant,
                EventId = "introskip.update",
                User = _userManager.GetUserById(session.UserInternalId),
                Item = episode,
                Session = session,
                Description = string.Format(
                    Resources.Notification_IntroUpdate_Description.Replace("\\n", Environment.NewLine),
                    episode.FindSeriesName(), episode.FindSeasonName(), introStartTime, introEndTime,
                    session.UserName)
            };
            _notificationManager.SendNotification(request);
        }

        public async Task CreditsUpdateSendNotification(Episode episode, SessionInfo session, string creditsDuration)
        {
            Resources.Culture = Thread.CurrentThread.CurrentUICulture;

            if (CanDisplayMessage(session))
            {
                var message = new MessageCommand
                {
                    Header = Resources.PluginOptions_EditorTitle_Strm_Assistant,
                    Text = string.Format(
                        Resources.Notification_CreditsUpdate_Message, episode.FindSeriesName(), episode.FindSeasonName()),
                    TimeoutMs = 500
                };
                await _sessionManager.SendMessageCommand(session.Id, session.Id, message, CancellationToken.None);
            }

            var request = new NotificationRequest
            {
                Title =
                    Resources.PluginOptions_EditorTitle_Strm_Assistant,
                EventId = "introskip.update",
                User = _userManager.GetUserById(session.UserInternalId),
                Item = episode,
                Session = session,
                Description = string.Format(
                    Resources.Notification_CreditsUpdate_Description.Replace("\\n", Environment.NewLine),
                    episode.FindSeriesName(), episode.FindSeasonName(), creditsDuration, session.UserName)

            };
            _notificationManager.SendNotification(request);
        }

        public async Task SendMessageToAdmins(string text, long? timeout)
        {
            var message = new MessageCommand
            {
                Header = Resources.PluginOptions_EditorTitle_Strm_Assistant,
                Text = text,
                TimeoutMs = timeout
            };

            var admins = LibraryApi.AllUsers.Where(kvp => kvp.Value).Select(kvp => kvp.Key);
            var sessions = _sessionManager.Sessions.Where(CanDisplayMessage)
                .Where(s => admins.Any(u => s.ContainsUser(u.InternalId)));

            foreach (var session in sessions)
            {
                await _sessionManager.SendMessageCommand(session.Id, session.Id, message, CancellationToken.None);
            }
        }

        private bool CanDisplayMessage(SessionInfo session)
        {
            return session.SupportedCommands.Contains("DisplayMessage");
        }
    }
}
