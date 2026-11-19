using Emby.Notifications;
using MediaBrowser.Controller;
using StrmAssistant.Properties;
using System.Collections.Generic;

namespace StrmAssistant.Notification
{
    public class CustomNotifications : INotificationTypeFactory
    {
        private readonly IServerApplicationHost _appHost;

        public CustomNotifications(IServerApplicationHost appHost) => _appHost = appHost;

        public List<NotificationTypeInfo> GetNotificationTypes(string language)
        {
            var types = new List<NotificationTypeInfo>
            {
                new NotificationTypeInfo
                {
                    Id = "favorites.update",
                    Name = Resources.Notification_CatchupUpdate_EventName,
                    CategoryId = "strm.assistant",
                    CategoryName = Resources.PluginOptions_EditorTitle_Strm_Assistant
                },
                new NotificationTypeInfo
                {
                    Id = "introskip.update",
                    Name = Resources.Notification_IntroSkipUpdate_EventName,
                    CategoryId = "strm.assistant",
                    CategoryName = Resources.PluginOptions_EditorTitle_Strm_Assistant
                }
            };

            // 与完整社区版一致：深度删除的通知类型跟着功能开关走，没开就不暴露
            if (Plugin.Instance?.GetPluginOptions()?.ExperienceEnhanceOptions?.EnableDeepDelete == true)
            {
                types.Add(new NotificationTypeInfo
                {
                    Id = "deep.delete",
                    Name = Resources.Notification_DeepDelete_EventName,
                    CategoryId = "strm.assistant",
                    CategoryName = Resources.PluginOptions_EditorTitle_Strm_Assistant
                });
            }

            return types;
        }
    }
}
