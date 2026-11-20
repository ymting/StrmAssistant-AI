# Emby 神医助手

![logo](StrmAssistant/Properties/thumb.png "logo")

## [[English]](README.en.md) （上游原版说明，本分支以中文为准）

> 基于社区版 2.0.0.27 的**个人修改分支**，由本仓库独立维护，让插件能在 Emby 4.9+ 上继续用。当前版本 **2.1.3**（tag `v2.1.3`）。

## 快速开始

1. 到 [Releases](https://github.com/ymting/StrmAssistant-AI/releases/latest) 下载 `StrmAssistantLite.dll`
2. 放进 Emby 配置目录的 `plugins` 文件夹，确保文件所有者/权限与同目录其它插件一致
3. 重启 Emby，在插件设置页里按需开启功能

> 本分支**只支持 Emby 4.9.0.0 及以上**；4.8.11 及更早请用官方社区版 2.0.0.30。
> 插件自带的「更新本插件」计划任务已从本仓库 Release 拉取新版，不会把上游那份只兼容到 4.8.11 的 dll 拉回来覆盖。

### 比官方社区版多了什么

| 功能 | 说明 |
| --- | --- |
| 4.9 / 4.10 / 4.11 适配 | 按新接口重写反射调用与参数绑定，4.8 之后不再崩 |
| 中文搜索增强（含拼音搜索） | `simple` 分词器，支持中文分词、全拼连写与首字母 |
| 阻止指定插件自动在线更新 | 填插件名即可，设置页会显示命中与拼错的条目 |
| 网络代理 | Emby 出站请求统一走代理，支持连通性检测 |
| 第三方插件适配 | MovieDb 按接口匹配 provider，冲突插件检测告警 |
| 设置页与计划任务全量中文化 | 界面文案、任务名均为中文 |
| 隐藏合集媒体库 | 阻止 Emby 扫库时自动新建「合集」库，并从用户视图隐藏（设置页可开关） |
| 媒体深度删除 | 删除媒体时级联清理 `.strm` / 软链指向的真实挂载文件、关联文件与空目录（设置页可开关，默认关闭） |

## 兼容性

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

**自行编译**

```bash
dotnet build StrmAssistant/StrmAssistant.csproj -c Release
```

默认会通过 ILRepack 把依赖合并输出到 `%AppData%\Emby-Server\programdata\plugins\StrmAssistantLite.dll`；
只想要编译产物可加 `-p:SkipPostBuild=true`，结果在 `StrmAssistant/bin/Release/netstandard2.1/`。
注意单纯 `SkipPostBuild=true` 的产物**不含 Harmony**，不能直接用，需要自行合并 `0Harmony.dll` 与 `ChineseConverter.dll`。

### 第三方插件适配

插件设置页顶部新增 **第三方插件** 区块，列出已检测到的相关插件及其版本。

| 插件 | 与本插件的关系 | 处理方式 |
| --- | --- | --- |
| **MovieDb** | 「演职人员增强」通过它的 Person provider 取数据 | 由原先「精确匹配类名 `MovieDbPersonProvider`」改为 **按接口 + 类名/程序集名/显示名三重匹配**，MovieDb 升级改名也不会静默失效；设置页直接显示「演职人员接口已匹配 / 未匹配」 |
| **InfuseSync** | 存在已知线程安全冲突 | 检测到即标黄告警，不做魔改 |
| **StrmExtract** | 功能重叠，不能共存 | 检测到即标黄告警 |
| **OMDb / OpenSubtitles** | 无代码依赖 | 仅显示版本，供排查评分/字幕来源 |
| **DataExplorer2** | 用于注入右键菜单命令 | 存在则注入，不存在自动跳过 |

> 说明：本分支只对 **MovieDb** 做了代码级适配（因为只有它被实际调用）。InfuseSync / OMDb / OpenSubtitles
> 在 Lite 代码里没有依赖，能做的只有「检测 + 告警」，不具备 PRO 那种魔改共存能力。

### 网络代理

设置页新增 **网络** 区块（移植自完整社区版的 `NetworkOptions`）：

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

设置页新增 **中文搜索** 区块（移植自完整社区版 `EnhanceChineseSearch`）：

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

**拼音搜索实测**（直接加载 `libsimple.so` 建 FTS5 表验证，非推测）：

| 输入 | 命中 | 说明 |
| --- | --- | --- |
| `zhangwei` | 张伟的夏天 | 全拼连写 |
| `zhang wei` | 张伟的夏天 | 空格分音节 |
| `zw` | 张伟的夏天 | 拼音首字母 |
| `zhizhuxia` | 蜘蛛侠：英雄无归 | 全拼连写 |
| `蜘蛛` | 蜘蛛侠：英雄无归 | 中文分词 |

机制：索引侧由分词器写入拼音 token，查询侧把 `match @SearchTerm` 换成 `match simple_query(@SearchTerm)`，
由扩展把连写的字母串展开成音节组合（`zhangwei` → `zhang+wei*` / `z+h+a+n+g+...` 等多路 OR）。
所以**拼音搜索必须在「Enhance Chinese Search」开启并完成索引重建后才生效**，开关关着走官方 `unicode61`，没有拼音。

> 已在 Emby **4.10.1.0** 真机完成索引重建与拼音搜索验收：`xianni` 命中「仙逆」73 条、
> `zhizhuxia` 命中「蜘蛛侠」；4.9.1.90 的 `fts_search9` 表与补丁入口也已核对。
> 开启前仍建议先备份 `data/library.db`。

### 插件更新屏蔽

设置页 **体验增强** 区块新增 **阻止插件自动在线更新**
（移植自完整社区版 `SuppressPluginUpdate`）：

| 项 | 说明 |
| --- | --- |
| 阻止插件自动在线更新 | 插件名或 dll 文件名，逗号或分号分隔，例如 `MovieDb,Tvdb`；默认为空（不屏蔽） |
| 插件更新屏蔽状态 | 保存后直接显示命中情况：命中的插件（带版本）与**没有匹配上的条目**——写错名字会立刻标黄 |

实现方式：给 `InstallationManager.GetAvailablePluginUpdates` 挂 Harmony Postfix，把命中的包从「可更新列表」里摘掉。
Emby 的「检查插件更新」计划任务与插件管理页的更新列表走的都是这个方法，因此两处同时生效。

- 只拦**在线更新候选**：手动上传 dll、手动在插件页点某个插件的「更新」都不受影响
- **不拦 Emby 服务端自身更新**（走 `GetAvailablePackages`，与本补丁无关）
- 名字匹配不区分大小写，`MovieDb`、`MovieDb.dll`、插件 GUID 三种写法都认
- 名单里写了本插件（`StrmAssistantLite`）时，「Update Plugin」计划任务同样会跳过

### 深度删除（2.1.3 新增）

设置页 **体验增强** 区块新增 **启用深度删除**（移植自完整社区版 `EnableDeepDelete`，默认关闭）：

删除媒体时，顺着 `.strm` 文件或软链接找到真正挂载在别处的目标文件，
连同它的字幕、nfo、图片等关联文件和清空后的空目录一起级联删除。
Emby 自带的删除只会删库记录与库目录下的 `.strm` / 软链本身，剩下的挂载目标不会被清掉。

| 项 | 说明 |
| --- | --- |
| 启用深度删除 | 默认关闭；保存后立即生效，不需要重启 |
| 生效条件 | 删除时勾选了 Emby 的「删除文件」；未勾选时只删库记录，不动磁盘 |
| 覆盖范围 | Emby 通用「删除」菜单 + 插件的「删除版本」按钮 |
| 不删什么 | http(s) 等远程资源只记录不删；普通媒体文件交给 Emby 自己删，不重复删 |
| 删除通知 | 给管理员发一条「媒体深度删除」，列出解析到的全部挂载路径 |

> ⚠️ 这是**真删磁盘文件**的功能。开启前先确认挂载目录映射正确，建议先在测试媒体库验证。

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
16. 阻止指定插件自动在线更新
17. 媒体深度删除

## 安装与使用说明

本页「快速开始」与各功能章节即为本分支的说明；上游 [Wiki](https://github.com/sjtuross/StrmAssistant/wiki) 对应的是原版功能，部分内容对本分支不适用，仅供对照。

## 关于本仓库

这是基于社区版 2.0.0.27 的**个人修改分支**，由本仓库独立维护，与上游社区版作者、Emby LLC 均无关联。
本分支**不接受赞赏或赞助**，也不提供收费服务；如果对你有帮助，点个 ⭐️ 就够了。

- 问题反馈与功能建议请到本仓库 [Issues](https://github.com/ymting/StrmAssistant-AI/issues)，不要提到上游仓库
- 说明以本页（中文 README）为准；[English](README.en.md) 是上游原版说明，部分内容已过时，仅供参考
- 编译产物只在本仓库 [Releases](https://github.com/ymting/StrmAssistant-AI/releases) 发布，下载文件名固定为 `StrmAssistantLite.dll`

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

