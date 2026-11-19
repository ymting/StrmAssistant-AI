# Emby神医助手

![logo](StrmAssistant/Properties/thumb.png "logo")

## [[English]](README.en.md)

## 兼容性（本分支）

> 本仓库是基于社区版 2.0.0.27 的修改版，唯一目的是让插件能在 **Emby 4.8.11.0 之后的版本**上正常工作。

| Emby 版本 | 状态 |
| --- | --- |
| 4.9.0.0 ~ 4.11.x | ✅ 支持（已按 4.9+ 接口重新适配，并用 4.9.1.90 / 4.11 参考程序集验证编译） |
| 4.8.11.0 及更早 | ❌ 不再支持，请使用官方社区版 2.0.0.30 |

**本次适配涉及的 Emby 变更**

- `ILibraryManager.GetExternalSubtitleFiles(long)` 被移除 → 改用 `IItemRepository.GetExternalTracks`
- `IItemRepository.GetChapters(long, MarkerType[])` → 新增 `CancellationToken` 参数
- `IMediaSourceManager.GetStaticMediaSources` 重载在 4.8 / 4.9.0.x / 4.9.1+ 各不相同 → 改为按参数名动态绑定
- `IMediaMountManager.Mount` 首参由 `ReadOnlyMemory<char>` 改回 `string`，`IMediaMount.MountedPath` 改为 `MountedPathInfo`
- `Emby.Providers` 内部类型 `AudioFingerprintManager` / `ThumbnailGenerator` / `SubtitleResolver` 的构造函数与方法签名变更 → 改为「按可满足的依赖挑构造函数 + 按参数名绑定实参」

**安装**

1. 编译或下载本仓库的 `StrmAssistantLite.dll`
2. 放入 Emby 配置目录下的 `plugins` 目录，确保文件所有者/权限与同目录其它插件一致
3. 重启 Emby

> 插件的「更新插件」计划任务已改为指向本仓库，不会再把上游那份只兼容到 4.8.11 的 dll 拉回来覆盖。

**自行编译**

```bash
dotnet build StrmAssistant/StrmAssistant.csproj -c Release
```

默认会通过 ILRepack 把 `ChineseConverter.dll` 合并输出到 `%AppData%\Emby-Server\programdata\plugins\StrmAssistantLite.dll`；
只想要编译产物可加 `-p:SkipPostBuild=true`，结果在 `StrmAssistant/bin/Release/netstandard2.1/`。

### 第三方插件适配

插件设置页顶部新增 **Third-party Plugins** 区块，列出已检测到的相关插件及其版本。

| 插件 | 与本插件的关系 | 处理方式 |
| --- | --- | --- |
| **MovieDb** | 「演职人员增强」通过它的 Person provider 取数据 | 由原先「精确匹配类名 `MovieDbPersonProvider`」改为 **按接口 + 类名/程序集名/显示名三重匹配**，MovieDb 升级改名也不会静默失效；设置页直接显示 `Person provider matched / NOT matched` |
| **InfuseSync** | 存在已知线程安全冲突 | 检测到即标黄告警，不做魔改 |
| **StrmExtract** | 功能重叠，不能共存 | 检测到即标黄告警 |
| **OMDb / OpenSubtitles** | 无代码依赖 | 仅显示版本，供排查评分/字幕来源 |
| **DataExplorer2** | 用于注入右键菜单命令 | 存在则注入，不存在自动跳过 |

> 说明：本分支只对 **MovieDb** 做了代码级适配（因为只有它被实际调用）。InfuseSync / OMDb / OpenSubtitles
> 在 Lite 代码里没有依赖，能做的只有「检测 + 告警」，不具备 PRO 那种魔改共存能力。

### 网络代理

设置页新增 **Network** 区块（移植自完整社区版的 `NetworkOptions`）：

| 项 | 说明 |
| --- | --- |
| Enable Proxy Server | 打开后 Emby 的出站请求全部走下面的代理 |
| Proxy Server | `http://host:port`、`https://host:port`，支持 `http://user:pass@host:port` |
| Ignore Certificate Validation | 代理做 MITM 时才需要打开 |
| Proxy Server Status | 打开设置页时自动做一次 TCP 连通性检测，显示延迟或不可达 |

实现方式：给 `ApplicationHost.CreateHttpClientHandler` 挂 Harmony Postfix，Emby 每造一个 Handler 就把
`WebProxy` 塞进去，因此 TMDB / 豆瓣 / 插件下载等所有走 Emby 内置 HttpClient 的请求都会走代理，不改业务代码。

> Emby 4.9 这里造的是 `SocketsHttpHandler`（4.8 是 `HttpClientHandler`），插件对两者都做了处理。
> 内网地址（10.x / 172.16-31.x / 192.168.x）默认直连，不走代理。

### 中文搜索增强

设置页新增 **Chinese Search** 区块（移植自完整社区版 `EnhanceChineseSearch`）：

| 项 | 说明 |
| --- | --- |
| Enhance Chinese Search | 把 FTS 索引换成自定义 `simple` 分词器，支持中文分词与拼音搜索 |
| Exclude Original Title | 搜索结果中排除 OriginalTitle |
| Search Scope | 按标题搜索时默认纳入的条目类型，留空为全部 |

**开与关都必须重启，且顺序不能省：**

1. **开启**：释放 `plugins/libsimple.so` → 加载扩展 → 把 `fts_search9` 切成 `simple` → **重建索引**
   （条目多时几分钟，期间搜索为空，日志会打 `Filling fts_search9 Start/Complete`）
2. **关闭**：保存后只是标记 restore，**必须重启 Emby** 才会把 tokenizer 换回
   `unicode61 remove_diacritics 2` 并重建。跳过这步会留下「表指向已不存在的分词器」，
   搜索会直接失效 —— 这正是手动替换 dll 不按流程操作会踩的坑
3. 平台不支持 / `libsimple.so` 释放失败时**拒绝启用**，不会留下坏状态

内嵌四个平台的原生扩展：`win_x64` / `linux_x64` / `linux_arm64` / `osx_universal`，哈希与完整版一致。

> 已验证 Emby 4.9.1.90 的 `fts_search9` 表与四个 patch 方法都在，但**尚未在真机跑过索引重建**
> （完整版作者只验证到 4.9.0.48）。开启前请先备份 `data/library.db`。

## 用途

1. 提高首次播放的起播速度
2. 视频截图预览缩略图增强
3. 片头片尾探测增强
4. 自动合并同目录视频为多版本
5. 独占模式提取媒体信息
6. 独立的外挂字幕扫描
7. 自定义刮削备选语言
8. 使用替代`TMDB`配置
9. 演职人员增强`TMDB`
10. 获取原语言海报
11. 中文搜索增强
12. 拼音首字母排序
13. 媒体信息持久化
14. 支持代理服务器
15. 支持`TMDB`剧集组刮削

## 安装与使用说明请查看 [Wiki](https://github.com/sjtuross/StrmAssistant/wiki)

## 赞赏

如果这个项目对你有帮助，不妨请我喝杯咖啡。如果你欣赏这个项目，欢迎为它点亮一颗⭐️。感谢你对开源精神的认可与支持！

![donate](donate.png "donate")

## 声明

本项目为开源项目，与 Emby LLC 没有任何关联，也未获得 Emby LLC 的授权或认可。本项目的目的是为合法购买并安装了 Emby 软件的用户提供额外的功能增强和使用便利。

### 使用须知

1. **合法使用**  
   本项目仅适用于合法安装和使用 Emby 软件的用户。使用本项目时，用户需自行确保遵守 Emby 软件的服务条款和使用许可协议。

2. **非商业用途**  
   本项目完全免费，仅限个人学习、研究和非商业用途。严禁将本项目或其衍生版本用于任何商业用途。

3. **不包含 Emby 专有组件**  
   本项目未包含 Emby 软件的任何专有组件（例如：DLL 文件、代码、图标或其他版权资源）。使用本项目不会直接修改或分发 Emby 软件本身。

4. **功能限制**  
   本项目不会绕过 Emby 的授权机制、数字版权保护 (DRM)，或以任何方式解锁其付费功能。本项目仅在运行时动态注入代码，且不会篡改 Emby 软件的核心功能。

5. **用户责任**  
   用户在使用本项目时，需自行承担遵守相关法律法规的责任。如果用户使用本项目违反了 Emby 的服务条款或相关法律法规，本项目开发者概不负责。

### 免责声明

1. 本项目开发者不对因使用本项目而可能导致的任何直接或间接后果（包括但不限于数据丢失、软件故障或法律纠纷）负责。
2. 如果认为本项目可能侵犯相关方的合法权益，请与开发者取得联系。

### 星星数

[![Star History Chart](https://api.star-history.com/svg?repos=sjtuross/strmassistant&type=Date)](https://www.star-history.com/#sjtuross/strmassistant&Date)
