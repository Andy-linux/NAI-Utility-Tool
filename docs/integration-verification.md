# 双端联调验证

## MODEL / UI 实施与证据

### 本轮改动
- Issue2：关闭时通过 `WorkspaceUnsavedSummary` 汇总图生图、超分、后处理工作区，不依赖当前页面；对8种dirty组合有实际单元断言。
- Issue6/7：`ModelCapabilities` 为UI/请求共用能力表，V5禁用Variety；sigma按官方实际尺寸floor/8与104×152比例开方，V3/V4基础19、V4.5基础58。SMEA、dynamicSMEA、Variety元数据独立解析，不互相推断。
- Issue11：`NovelAiPromptPresets` 使用公开官方bundle的quality/UC字符串ID、精确标签与全局numeric hint；UI使用模型对应选项，旧UcPreset0/1/2迁移到heavy/light/none。V5标准/轻度质量标签、人物/兽人UC支持保存与导入；未知UCID安全回退。
- Issue12：新V5 Full High默认23步；其他模型新建默认28；显式保存步数原样保留。
- Issue13（Release140 修订）：发送按钮不再显示“0 Anlas / Opus 免费”；已知零费用时显示短“0 Anlas · 发送”，可靠付费预测显示“≈ N Anlas”。额度浮窗仅保留 Anlas/V5 余额、进度和功能标签，管理右侧提供刷新图标；共享钱包边界只在文档说明，不累加潜在重复钱包。不伪造 V5 单次百分比公式。
- 额度在启动、保存实际 API/账号变更、业务返回和手动点击时更新；打开浮窗、工作区导航及单纯时间流逝不请求。业务结果失效标志与原 RefreshLock 保证连续请求等待在飞刷新，身份归属校验保留。
- Effort：模型框下紧凑High/Medium双选，仅Full及对应infill；Medium有效请求ID与14步/Euler/fixedheavyUC遵循官方证据；steps/sampler/UC/rescale灰化，CFG Scale仍可调。Stored High参数及主/角色负面词不清空，切换及设置保存不覆盖。生成、Enhance、inpaint/img2img结果加入client-only草稿元数据，历史导入可恢复High草稿；外部Medium无草稿时保留当前High草稿。client-only字段不进入上游请求，隐私模式不加入。
- Enhance：普通尺寸采用官方 nearest64（中点向上）取整再准备输入；832×1216 的默认1.5倍为1280×1856。保留流式偏好；Max沿用原协议。
- API设置移除非必要的五个超时输入框，保留现有超时配置字段及请求预算；坏URL提交前验证，恢复备份时保留本地化通知。
- 独立审阅补丁：UI 精确参考与实际请求同样取消 Opus 基础免费；Medium 不运行 High 角色 UC 前缀重写；历史草稿负面词只捕获实际启用且发送的角色顺序，不将禁用角色负面词错配到其他角色；Medium 保存保护设置不破坏 High Steps 草稿。
- 自动化恢复用户配置的HTTP状态重试次数；无HTTP状态的超时/无final仍为0次。service单次调用不自动重试。

### 官方依据
详见工作区 `integration/evidence/official-protocol.md`：最新APP、GEN、COST公开静态资源及docs。bundle比文档存在差异的quality标签以同版本实际请求实现为准。静态协议核验不等于带认证真实调用成功。

### 实际执行（持续更新，非历史数字）
- 本地官方SDK9.0.301，`Debug | x64` Windows构建成功，最近一次0警告/0错误。
- `src/NAITool.ModelTests`：100实际断言通过（主会话最终统一回归），包括默认/保存、Medium快照隔离、费用、sigma、全部已支持模型UCID标签与往返、dirty工作区、Enhance500。
- `tests/AnlasPricing`：42实际检查通过；未知V5免费资格用例已修正。
- `tests/Seeds`：最新实际145seed+53模型+95角色+38选择导入通过（含localMedium草稿2项）。
- `tests/Enhance`：56实际检查通过（内存HTTP mock，无账户数据或真实网络），更新V5删除SMEA字段及单结果ZIP不附原图的旧断言。

### 主会话最终检查点回归
`integration/run-client-regressions.ps1` 的15个项目全部成功：Persistence53、Network77、Model100、Download53、ApiAccounts227、ApiTransport129、Anlas42、Enhance56、Seeds145+53+95+38、Upscale55、History207、Windows thumbnails11、LocalUpscale80、MaskEditing9806、PreviewPan36。LocalUpscale real-model smoke=false，不等于GPU真实模型验证。首次统一执行的 ApiTransport partial-decrypt fixture 失败已分离整文件有效备份恢复与无备份部分解密两种测试，最终重新统一执行全通过。Debug x64 当前整合0warnings/0errors。

主会话真实 paired HTTP mock 最终93 assertions通过（包括WebP解码、当前Medium fixed-heavy有效请求、原生final/error/EOF/截断、旧完整URL/root、凭证隔离）；不等于官方收费请求通过。Release dirty候选已真实启动，初步真实UIA High→Medium→High与切模型证据留在工作区；高级popup位置/截图、最新全矩阵及最终clean-SHA包留下一轮非Fast终验，当前不是正式发布。

### Release140 本轮补充（2026-10-11）
- 已授权的隔离 WinUI 客户端真实 V5 High/Medium 流式生成成功；29 步付费生成 UI 预估约 30 Anlas，前后余额差 30。手动刷新控件已真实点击。
- 真实 C# service 的图生图、V4.5 Vibe 编码/使用、官方 Upscale、文本以及非流式 Enhance 成功；这些 service 验证不冒充对应 WinUI 全流程验收。
- 默认1.5倍 Enhance 的尺寸问题已按官方 nearest64 修正；既有生产证据确认1280×1856普通/流式两路均成功，因此撤销禁用Enhance流式的退让。未重复发收费请求。
- 本轮未修改/重启生产服务。完整交付状态与 clean-SHA 包见工作区 integration/FINAL_QUOTA_RELEASE_RESULTS.md。

### 明确未验证
- 未完成六个WinUI人工点击场景、实际用户工作区关闭dialog、backup恢复dialog；编译成功不代表这些人工行为验证完成。
- V5精确每次percent扣減算法没有可验证依据，仅显示余额、不显示单次用量估价，绝不固定High30/Medium18全局价格。

## NETWORK / 持久化 / 下载一致性

- 所有已实现业务共用 `ApiEndpointResolver`，自定义根地址与旧完整 `/ai/generate-image` 地址安全归一化；拒绝 URL 内嵌凭证/query/fragment，不携带下游 Key 回退官方。官方主机有当前公开 bundle 依据。
- 普通生成、inpaint、img2img、原生 stream、Vibe、官方2x超分、提示词文本及账户读取均使用真实 service 方法。原生 stream 是 `parameters.stream=msgpack`、4-byte BE + MessagePack `event_type`，只接受单结果 final；SSE 兼容独立解析。预览后 error/EOF/截断不成功。请求选择的 msgpack 不凭未知 MIME 杜撰分派；明确 JSON/HTML 错误表示拒绝。
- 当前公开官方 constructor 只证实 WebP wire，请求统一 `image_format=webp`，而不是从 UI PNG 偏好猜 API 枚举；client-only High 草稿 metadata 在实际解码后本地转换 PNG 再写入，PNG writer 校验输入签名，避免 WebP 被当 PNG chunk 破坏。最终文件本地 PNG/WebP 偏好另行处理。
- ZIP 单结果协议明确 `n_samples=1`，不附 original image，不静默取首项；多个有效结果报错。ZIP 数量/字节/解码像素有本地安全上限，官方超分按已核验 `image*.png` 筛选。
- 等待+业务总预算包括账户预检到流式 body，账户查询另有短预算；本地图片准备不计入网络超时。没有 queue-start 信号，不声称客户端独立精确计时两个阶段。取消/超时/残缺响应后的扣费可未知，不自动付费重试。
- 账户缓存以 endpoint+credential hash 运行时身份验证，不按 UI 行号；旧网关404账户返回 unavailable，不伪造0。V5 isNegative 与 percent 独立，已耗尽仍可用足够 Anlas；已知精确参考取消 Opus 基础免费，额外参考收费独立。
- `AtomicJsonFile` 同目录临时写入、flush-to-disk、原子替换和有效 schema/DPAPI 备份恢复。支持旧单账户加密迁移；备份不复制旧明文 Key。两份均无法解密时保留 ciphertext，普通主题/提示词仍可保存；原界面可替换/删除凭据。文件间不是一个事务。
- 模型下载独立临时文件/长度和可用 checksum 验证、实际 CPU ONNX graph 与 CSV 校验、原子发布和 completeness manifest。中断发布 marker 使混合模型不可用，重试可修复；缓存不保证抗恶意保持时间戳篡改，首次冷校验同步及大模型性能未验证。

### 用户澄清后的最小修正
- 恢复Anlas余额可生成张数与V5 High参考张数（官方FAQ：High23步、约1MP，100%约1730张），V5零Anlas不显示无限张；未知单次V5百分比不显示。
- 删除任意16GiB模型下载限制，保留长度/校验/下载完成后发布；移除责任式注释，不新建生产类型或框架。
- 当前工作树由协调实际执行Debug x64（0警告/0错误）、15项目全通过及paired mock93通过；证据在工作区 `.runs/user-steer-coordinator-regressions/`。最新源码SHA、净包与聚焦GUI结果以 `integration/FINAL_QUOTA_RELEASE_RESULTS.md` 为准，以下旧阶段说明不作最新验收结论。

## Mock / 发布边界

工作区 `integration/evidence/paired-mock-results.md` 记录实际生产 service 源码 → NyaProxy 进程 → localhost 合成上游的 HTTP 断言；UI/本地化依赖有明确 stub，不是 GUI 验收。发布及可视证据另见 `integration/evidence/ui-release-results.md` 和 `ui-acceptance/`。发布候选尚待精确提交 SHA 的干净重打包、真实窗口操作与最终复测；不能把 Debug 构建或 static XAML 当正式 UI 验收。

`/ai/augment-image` 有 resolver/网关固定业务路由，但没有新增客户端 Director API/UI，因为完整工具参数约束未核验。可选 `/oa/v1/models` 没有官方依据，未启用；suggest-tags query 静态核验不等于已实现调用，现有本地 CSV 补全保留。实际官方 stream MIME、服务器 legacy JSON 当前接受性、收费余额变化、生产排队/部署均未通过本轮真实账户请求验证。Enhance500 是本地尺寸规则及 mock 验收，官方真实服务对非64倍数图像的实际接受性未发收费请求确认。
