using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using MediaBrowser.Model.IO;

namespace StrmAssistant.Common
{
    public static class CommonUtility
    {
        /// <summary>
        /// 按 FullName 去重 FileSystemMetadata。深度删除会把「目标文件 + 关联文件 + 各级父目录」
        /// 汇总到同一个集合，同名路径可能来自多个媒体源，必须按路径去重后再删。
        /// </summary>
        public class FileSystemMetadataComparer : IEqualityComparer<FileSystemMetadata>
        {
            public bool Equals(FileSystemMetadata x, FileSystemMetadata y)
            {
                if (ReferenceEquals(x, y)) return true;
                if (x is null || y is null) return false;
                return string.Equals(x.FullName, y.FullName, StringComparison.Ordinal);
            }

            public int GetHashCode(FileSystemMetadata obj)
            {
                if (obj is null) throw new ArgumentNullException(nameof(obj));
                return obj.FullName?.GetHashCode() ?? 0;
            }
        }

        private static readonly Regex MovieDbApiKeyRegex = new Regex("^[a-fA-F0-9]{32}$", RegexOptions.Compiled);

        public static bool IsValidHttpUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;

            if (Uri.TryCreate(url, UriKind.Absolute, out var uriResult))
            {
                return uriResult.Scheme == Uri.UriSchemeHttp || uriResult.Scheme == Uri.UriSchemeHttps;
            }

            return false;
        }

        public static bool IsValidMovieDbApiKey(string apiKey)
        {
            return !string.IsNullOrWhiteSpace(apiKey) && MovieDbApiKeyRegex.IsMatch(apiKey);
        }

        public static bool IsValidProxyUrl(string proxyUrl)
        {
            try
            {
                var uri = new Uri(proxyUrl);
                return (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
                       (uri.IsDefaultPort || (uri.Port > 0 && uri.Port <= 65535)) &&
                       (string.IsNullOrEmpty(uri.UserInfo) || uri.UserInfo.Contains(":"));
            }
            catch
            {
                return false;
            }
        }

        public static bool TryParseProxyUrl(string proxyUrl, out string schema, out string host, out int port, out string username, out string password)
        {
            schema = string.Empty;
            host = string.Empty;
            port = 0;
            username = string.Empty;
            password = string.Empty;

            try
            {
                var uri = new Uri(proxyUrl);
                if (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                {
                    schema = uri.Scheme;
                    host = uri.Host;
                    port = uri.IsDefaultPort ? uri.Scheme == Uri.UriSchemeHttp ? 80 : 443 : uri.Port;

                    if (!string.IsNullOrEmpty(uri.UserInfo))
                    {
                        var userInfoParts = uri.UserInfo.Split(':');
                        username = userInfoParts[0];
                        password = userInfoParts.Length > 1 ? userInfoParts[1] : string.Empty;
                    }

                    return true;
                }
            }
            catch
            {
                // ignored
            }

            return false;
        }

        public static (bool isReachable, double? tcpPing) CheckProxyReachability(string host, int port)
        {
            try
            {
                using var tcpClient = new TcpClient();
                var stopwatch = Stopwatch.StartNew();
                if (tcpClient.ConnectAsync(host, port).Wait(999))
                {
                    stopwatch.Stop();
                    return (true, stopwatch.Elapsed.TotalMilliseconds);
                }
            }
            catch
            {
                // ignored
            }

            return (false, null);
        }

        public static (bool isReachable, double? httpPing) CheckProxyReachability(string scheme, string host, int port,
            string username, string password)
        {
            double? httpPing = null;

            try
            {
                var proxyUrl = new UriBuilder(scheme, host, port).Uri;
                using var handler = new HttpClientHandler();
                handler.Proxy = new WebProxy(proxyUrl)
                {
                    Credentials = !string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password)
                        ? new NetworkCredential(username, password)
                        : null
                };
                handler.UseProxy = true;

                using var client = new HttpClient(handler);
                client.Timeout = TimeSpan.FromMilliseconds(666);

                var task1 = client.GetAsync("http://www.gstatic.com/generate_204");
                var task2 = client.GetAsync("http://www.google.com/generate_204");

                var stopwatch = Stopwatch.StartNew();
                var completedTask = Task.WhenAny(task1, task2).Result;
                stopwatch.Stop();

                if (completedTask.Status == TaskStatus.RanToCompletion && completedTask.Result.IsSuccessStatusCode &&
                    completedTask.Result.StatusCode == HttpStatusCode.NoContent)
                {
                    httpPing = stopwatch.Elapsed.TotalMilliseconds;
                }
                else
                {
                    var otherTask = completedTask == task1 ? task2 : task1;
                    if (otherTask.Status == TaskStatus.RanToCompletion && otherTask.Result.IsSuccessStatusCode &&
                        otherTask.Result.StatusCode == HttpStatusCode.NoContent)
                    {
                        httpPing = stopwatch.Elapsed.TotalMilliseconds;
                    }
                }
            }
            catch
            {
                // ignored
            }

            return (httpPing.HasValue, httpPing);
        }

        public static string GenerateFixedCode(string input, string prefix, int length)
        {
            using var md5 = MD5.Create();
            var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(input));
            return prefix + BitConverter.ToString(hash).Replace("-", "").Substring(0, length).ToLower();
        }

        public static long Find(long x, Dictionary<long, long> parent)
        {
            if (parent[x] == x) return x;
            return parent[x] = Find(parent[x], parent);
        }

        public static void Union(long x, long y, Dictionary<long, long> parent)
        {
            var root1 = Find(x, parent);
            var root2 = Find(y, parent);
            if (root1 != root2) parent[root1] = root2;
        }

        /// <summary>
        /// 用 BCL 的 Directory 判断目录是否为空（只读）。深度删除的目标都是本地挂载路径，
        /// 不需要 IFileSystem 的凭据 / UNC 抽象，这里不引入额外依赖。
        /// </summary>
        public static bool IsDirectoryEmpty(string directoryPath)
        {
            if (!Directory.Exists(directoryPath))
                return false;

            var entries = Directory.EnumerateFileSystemEntries(directoryPath).Take(1);

            return !entries.Any();
        }

        /// <summary>
        /// 判断是否为符号链接（Windows junction / Unix symlink）。深度删除需要先认出软链，
        /// 才能顺着链接删到真实目标；普通文件交给 Emby 自己的删除流程，避免重复删除。
        /// </summary>
        public static bool IsSymlink(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;

            try
            {
                var info = new FileInfo(path);
                return info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 读取软链目标路径。netstandard2.1 里没有 FileInfo.LinkTarget（.NET 6 才引入），
        /// 直接写会编译不过，因此用反射取值：Emby 4.9 / 4.10 运行在较新运行时上能取到，
        /// 旧运行时取不到时返回 null，由调用方跳过该媒体源，不影响其它功能。
        /// </summary>
        public static string GetSymlinkTarget(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;

            try
            {
                var property = typeof(FileInfo).GetProperty("LinkTarget");
                return property?.GetValue(new FileInfo(path)) as string;
            }
            catch
            {
                return null;
            }
        }

        public static double LevenshteinDistance(string str1, string str2)
        {
            int n = str1.Length;
            int m = str2.Length;
            int[,] d = new int[n + 1, m + 1];

            for (int i = 0; i <= n; d[i, 0] = i++) ;
            for (int j = 0; j <= m; d[0, j] = j++) ;

            for (int i = 1; i <= n; i++)
            {
                for (int j = 1; j <= m; j++)
                {
                    int cost = (str1[i - 1] == str2[j - 1]) ? 0 : 1;
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                }
            }

            int levenshteinDistance = d[n, m];
            double similarity = 1.0 - (levenshteinDistance / (double)Math.Max(str1.Length, str2.Length));

            return similarity;
        }
    }
}
