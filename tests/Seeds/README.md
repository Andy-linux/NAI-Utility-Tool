# 元数据、请求输入与流式结果回归

测试读取已构建客户端程序集；不向 NovelAI 发送请求。

```powershell
dotnet publish src/NAITool/NAITool.csproj -c Debug -p:Platform=x64 -r win-x64 --self-contained true
$env:PATH = (Resolve-Path build/Debug/bin).Path + ";" + $env:PATH
dotnet run --project tests/Seeds -c Debug
```

使用其他构建目录时，传入 `-p:NaitoolBuildDirectory=<候选的 bin 目录>`，并把同一目录放入 PATH，供 Windows App SDK 原生依赖加载。

`Fixtures/WebsitePresetImports.json` 保存固定官网部署的导入输入与实际回调输出；覆盖预设提示优先级、正文边界、透明背景组合及跨型号 UC 映射。它验证导入后的语义状态，不代表拖拽手势、签名或真实生成验收。

原有 Seed、型号、角色和部分导入检查仍使用同一入口执行。

`StreamResultChecks` 通过网关 SSE 调用同一生产帧处理器和图片解码器，检查默认预览起点、缺少步数的兼容帧、最终图后预览、重复 final、缺失最终图、服务端错误及取消。它验证接收结果，不代表真实网络、计费、重试或界面时序已验收。
