# NAI Utility Tool mobile-dev：Android / iPhone / iPad 移植设计

> 2026-10-10。基线为 Andy-linux/NAI-Utility-Tool 的 dev 分支 cc28495；独立实验分支 mobile-dev。目标是个人开发试验，不改变 Windows WinUI 3 主线、不部署网关、更不做移动端模型推理。

## 结论与选择依据

**采用 Uno Platform 6.7.30（官方空白模板）、.NET 10（移动宿主）+ C# .NET 9 可移植共享核心，Skia renderer。** Windows 仍为原 WinUI 3 / Windows App SDK。生成调用是云端 NovelAI 原生请求或指向用户指定的 NyaProxy，不依赖 Windows 进程在线。Uno 支持 WinUI API/XAML 迁移，但现有 148 KB MainWindow.xaml、大量 MainWindow partial code-behind、Win2D/ComputeSharp/DirectML/Windows DPAPI/Registry 强依赖都不具备原样移动编译条件。把官方“99%复用”示例的数字直接套用到本项目是不准确的。

方法：按照官方 Uno 迁移指南建立新的单项目 mobile heads，而非把 PC 工程改成多平台。先用现有 dev 纯 C# 文件直接链接编译；只给新移动工程增加极小的 API 适配层和触控布局；真正使用新的画布时改底层图形 API，**不是迁移整个 Windows 主窗口**。

新宿主目前的源码位置：
- src/NAITool.Mobile/NAITool.Mobile：Uno XAML 手机/平板壳与 UI；
- src/NAITool.Mobile/NAITool.Mobile.Core：原 dev 纯 C# 模型、费用规则、URL、MessagePack/SSE/ZIP 等直接 Link 编译，另加 mobile-only 的 Generate transport；
- src/NAITool.Mobile/NAITool.Mobile.Core.Tests：无外部测试框架依赖的离线 HttpMessageHandler mock；
- 不改 Windows 原 NAITool.csproj / MainWindow 文件，不直接修改 NyaProxy。

## 基于 dev 的功能盘点与移植优先级

| 功能/具体实现 | 现有项目位置或依赖 | 手机/平板安排 | 当前实验范围 |
|---|---|---|---|
| NAI 3、4、4.5、5 模型、尺寸、Steps、Sampler/Scheduler、CFG、Seed | NAIParameters、ModelCapabilities、NovelAIService | P0，复用业务规则、重组 XAML 触摸表单 | 首版覆盖常用模型和主参数 |
| V5 High/Medium、质量与 UC preset、Variety 等模型能力约束 | ModelCapabilities、NovelAiPromptPresets、NovelAiAnlasCalculator | P0，保持 Medium 14 Steps / Euler、禁止不支持 UC/CFG Rescale，同时恢复 High 草稿 | 首版含 Effort/Quality/UC；Variety / SMEA UI 后补 |
| V4+/V5 角色词、角色负词、位置（V5 最多 22） | CharacterPromptRules、MainWindow.PromptWorkspace.Characters | P0/P1，保留协议和角色上限，位置编辑适合 iPad | 首版仅一个角色输入，未来动态增减角色 |
| API Root、账户切换、额度、请求超时、费用展示 | ApiEndpointResolver、ApiAccountRouter、NovelAIService、SettingsService | P0 单凭证；P1 多账户/额度/费用 UI | 单凭证、严格 URL 归一；不移植 DPAPI |
| 生图返回 ZIP / 原生 MessagePack streaming / SSE | NovelAIImageResponse、NovelAIImageStream/Frames、NovelAIMessagePack | P0，**直接链接原解析器**，仅 final 可保存 | Mock 可检验单图/中间帧不能冒充 final |
| 历史、收藏、图库、文件导出、元数据保留 | ImageMetadataService、History*、本地 output/ | P0 应用私有历史；P1 系统相册/分享、缩略图与收藏 | 首版私有目录按需读取，暂无系统相册授权 |
| Inpaint/重绘/降噪，画笔、橡皮、矩形遮罩、移动/缩放、撤销、导出 | MaskCanvasControl、MaskDocument（Win2D）、ViewTransform、UndoManager | P1，ViewTransform 可复用，Win2D render target 改 Skia；按原始像素导出 mask | 尚未进入 P0，严禁标称“已可重绘” |
| Vibe Transfer / Precise Reference、多参考、预编码缓存 | MainWindow.References、VibeCacheService | P1；优先远端编码、再考虑本地缓存 | 尚未实现 |
| Prompt 自动补全、权重高亮、快捷词、Wildcards、权重语法转换 | TagCompleteService、WildcardService、PromptTools | P1，可复用纯文本规则但输入法需真机验收 | 基础 Prompt/风格词输入 |
| 官方超分、Enhance、V5 Max | NovelAIService.Upscale、Generation.Enhance | P1 调云端 API，响应/费用严格跟随 dev | 尚未实现 |
| 后期效果链、滤镜、蒙版区域 | Win2dEffectsRenderer、CpuEffectsRenderer（SkiaSharp） | P2；可研究复用 CPU Skia 算法，但大图实时滤镜对手机负担高 | 不默认搭载 |
| 本地 ONNX 超分、Tagger 反推 | UpscaleService/ReverseImageTaggerService、DirectML | P2 单独研究 CoreML/NNAPI/CPU，视模型内存、许可/速度决定 | 明确不首版移植 |
| 批量生成、自动化预设、随机风格、后期链 | AutomationPresetService、MainWindow.Automation | P2；需要手机后台执行策略与电量评估 | 尚未实现 |
| SuperDrop、透明 UI、Windows 回收站、DPAPI | Windows Shell/Input/Win32 专有 | 不生搬硬套；系统分享/文件提供程序/平台钥匙串逐项代替 | 默认不开启 |
| 多语言/i18n、主题与素材 | assets/i18n、assets/svg、Assets | P1 逐步移植复用，手机布局优先保留桌面视觉语言 | 先中文、暗色系 |

P0 的验收闭环：**在 Android 的可安装应用或 Uno Skia Desktop 模拟宿主上输入 Prompt/参数/API Root/会话内 Token → 构造 dev 等价请求 → 向受控 mock HTTP 服务发起一次 POST → 严格解析 ZIP / streaming final → 预览 → 写入应用私有历史**。收费的真实 NovelAI 生图不属于本轮自动测试。

## 屏幕、触控、图片与性能设计

- **窄屏**（目前暂用逻辑宽度 < 840 DIP）：参数和预览切换为两个独立全屏工作区，底部固定生成/取消操作；避免把 148KB 桌面三栏 XAML 等比缩小。
- **大屏 / iPad 横屏**（>=840 DIP）：左侧可滚动 Prompt/参数，右侧稳定预览；历史独立列表。未来 Inpaint 时改成中央画布+浮动工具条/侧栏，可适配手写笔、悬停和双指手势。最终应测试 iPad Split View/Stage Manager、Android 大屏窗口尺寸变化，不应仅依靠设备名称。
- **低算力设备**：不下载 DirectML/ONNX 权重，不使用持续动画渲染；图像使用系统解码、单张预览和按需读取的至多 100 条历史列表；真正的缩略图缓存、分级 decode、内存阈值到 P1。HTTP 只有一个进行中的生图任务，不盲目自动重试已发送的收费请求。
- **未来蒙版编辑**：真实图像像素坐标应为唯一权威坐标；画布 ViewTransform 做 screen↔canvas 映射。建议 Skia SKCanvasElement 或相应移动端 Skia surface，笔迹记录为路径/增量块而不是沿用当前 1024² RGBA × 50 份全量 UndoManager 快照（约 200 MiB）。笔、双指缩放、单指涂抹/橡皮、手掌抑制需真机验证；蒙版 alpha 与 API 数据格式要同 Windows 版比对测试。
- **安全与平台集成**：本实验默认 Token 仅存 PasswordBox 内存，不落地、不记日志；正式本地持久化须用 Android Keystore / iOS Keychain。API Root 自定义模式不回退官方。使用 HTTPS（自签证书不得默认绕过验证）；Android/iOS HTTP 明文策略不能按 Windows 习惯处理。当前“保存”只进入应用沙盒，不声称已导入相册；系统相册/文件分享属于下一阶段。
- **性能口径**：图像生成仍在 NovelAI 服务器；手机侧优化的是 XAML/Skia 布局、网络流式处理、图片解码、缓存、画布绘制与内存。不要用服务器 GPU 速度推断手机资源占用。

## 实施批次（只设置产品完成条件，不设置机械时间门禁）

1. **P0 可执行**：Uno net10 共享 Android/iOS/desktop head；与现有 .NET9 dev 共编的 Core；基础 Generate UI/自定义 API/Medium-High/图片响应/mock；Android APK 作为先行交付物。iOS/iPadOS 在 Mac Xcode 编译/签名。
2. **P1 触控绘图与业务完善**：真实 iPhone/iPad 布局视觉验收，Skia mask editor/Inpaint、角色数量和位置、Vibe/Precise Reference、账户余额和费用、系统分享与历史图库、输入法/屏幕尺寸适配。每项都以真实交互与 API mock 回归为准。
3. **P2 可选择能力**：官方 Enhance/超分之后，才根据性能实测决定本地 Skia 后期、Tagger/ONNX、Wildcards/批量自动化；避免因为原 Windows 有功能就盲目移植移动端模型。

## 技术与许可约束

- dev 仓库 GPL-3.0；共享源码和改版继续保留 GPL 授权与原仓库出处；第三方 assets/tagsheet/ONNX 模型需另核许可。
- 不以 Windows 构建成功推断 Android/iOS 可用。Windows Android 构建需要 .NET Android workload、Android SDK、JDK；iOS IPA 需要 macOS、Xcode、有效签名配置与设备实测。
- Uno WinUI 兼容并非 Win2D/Windows App SDK 全平台实现；UIKit/Android 混合原生控件、IME、Apple Pencil 压感和键盘遮挡都是 P1 的真实设备检查项。
- 优先小型、可组合的共享源文件。后续 dev 更新协议时须同步更新 linked code 和 mobile transport；等稳定之后才考虑从 Windows 抽一个真正双端共享的 API 请求构造器，不能为了本次实验大改 PC 主分支。
- 不访问生产 NyaProxy / 38 节点进行收费/有副作用操作。

## 实际构建方法（Windows 工作区）

当前 Uno 单项目模板位置：src/NAITool.Mobile/NAITool.Mobile/NAITool.Mobile.csproj。可从 src/NAITool.Mobile 启动 SDK10 命令。共用 core 为 net9，适配 Windows 端仍用原 SDK9；模板 global.json 选择 Uno.Sdk 6.7.30。

~~~powershell
$env:DOTNET_ROOT = 'F:\nai\.tools\dotnet'
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
dotnet run --project src/NAITool.Mobile/NAITool.Mobile.Core.Tests/NAITool.Mobile.Core.Tests.csproj -c Release
dotnet build src/NAITool.Mobile/NAITool.Mobile/NAITool.Mobile.csproj -f net10.0-desktop -c Release -p:UseMonoRuntime=false
# 安装独立的 JDK21，ANDROID_HOME 指向已安装 API36 Platform 的 Android SDK
$env:JAVA_HOME = 'F:\nai\.tools\jdk21-dist\jdk-21.0.12.1+1'
$env:ANDROID_HOME = "$env:LOCALAPPDATA\Android\Sdk"
dotnet build src/NAITool.Mobile/NAITool.Mobile/NAITool.Mobile.csproj -f net10.0-android -c Release -p:MobileOnlyBuild=true -p:RuntimeIdentifiers=android-arm64 -p:RuntimeIdentifier=android-arm64 -p:AndroidPackageFormat=apk -p:JavaSdkDirectory="$env:JAVA_HOME" -p:AndroidSdkDirectory="$env:ANDROID_HOME"
~~~

iOS 目标框架在 macOS 上启用；在 Windows 上条件排除，避免没有 iOS workload 导致 Android/Desktop restore 失败。发布版 iOS 必须由 Mac/Xcode 编译和签名。

## 官方 / 一手资料

- https://platform.uno/docs/articles/migrating-apps.html — WinUI → Uno 迁移（注意示例复用率的前提）
- https://platform.uno/docs/articles/get-started-dotnet-new.html — 官方 Uno.Templates 和 CLI
- https://platform.uno/docs/articles/getting-started/wizard/using-wizard.html — .NET 10 与 mobile TFM
- https://platform.uno/docs/articles/features/using-skia-rendering.html — iOS/Android Skia backend
- https://platform.uno/docs/articles/controls/SKCanvasElement.html — SKCanvasElement 画布和硬件加速
- https://platform.uno/docs/articles/features/pointers-keyboard-and-other-user-inputs.html — 移动端 Pointer/输入事件覆盖
- https://learn.microsoft.com/en-us/dotnet/android/getting-started/installation/net-android — Android workload / SDK 安装
- https://developer.apple.com/design/human-interface-guidelines/ — iPhone/iPad HIG
- https://developer.android.com/guide/topics/large-screens/support-different-screen-sizes — Android 大屏与自适应布局
