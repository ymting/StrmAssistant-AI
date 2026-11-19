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
