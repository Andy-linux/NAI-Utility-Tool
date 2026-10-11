# NAI Utility Mobile Dev · 0.1.0-alpha.1

实验版 Android 客户端。原仓库：Andy-linux/NAI-Utility-Tool，来自 `dev` 基线 `cc28495`，所有新增代码仅在 `mobile-dev` 分支。

## 此版可试的功能
- Uno Platform + .NET10 / Skia 手机与平板响应式壳，基础暗色布局、底部切换生成/预览/重绘/历史。
- NovelAI 文生图基础 Prompt / Negative / Style，常用模型、尺寸、Steps / CFG / Seed / Sampler / Scheduler，V5 High/Medium；
- 动态角色与坐标（V5 最多22；按模型限制提交角色）；生成图片预览、应用内保存、文件导出及结果转入重绘工作区；
- 自定义 API URL / NovelAI / 兼容 NyaProxy（API Key 当前只存在会话内存），Native MessagePack 和 ZIP 解析；
- 初步 Inpaint 画布（导入、笔刷、橡皮、撤销/重做、双指缩放、mask WebP 导出）。这是 **未完成验收的试验功能**。

## 明确缺项与风险
- 用户指定的核心 A 阶段：Prompt 自动补全/权重辅助、手动宽高/高级参数、PNG/NAI 元数据导入尚未完成。
- Inpaint 原图坐标与有效像素遮罩、笔刷/橡皮和安卓真机手势须继续验证，图生图降噪没有独立完整工作流；可能存在不可用功能。
- Anlas / V5 账号真实额度、官方 Enhance / Upscale、官方文本辅助、提示词收藏管理、完整图库、Vibe / Precise Reference 暂未完成。
- 未进行真实收费 NovelAI API 测试。已知移动端与桌面生成请求实现仍有代码重复，后续可能需要协议差异修补。
- iPhone/iPad 未编译 IPA；需要 macOS + Xcode。Android 签名为**本机调试用证书**，仅供自测。
- 安装 ARM64 APK 仅适合 ARM64 Android 真机；x64 APK 仅用于模拟器或 x64 环境。
- 本次发布只用于尽快提供一版可安装的基础原型。**不可理解为全部核心功能已完成或已验收。**

## 测试、许可证
- 本次本地核心模拟请求共13项离线测试，含 V5/角色/URL/ZIP/MessagePack/Inpaint 请求合同；但这些不是实机收费生产证明。
- Android APK 包名 `com.andy.naiutility.mobiledev`，版本 `0.1.0`，以 GPL-3.0 开源，原版保留版权与来源。
- 新范围清单详见 `docs/mobile/SCOPE_20261011.md`，正在依序推进首批生成核心、重绘/图库/任务管理、官方功能与常用辅助。
