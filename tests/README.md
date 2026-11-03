# 测试

## wordbuf —— 核心逻辑单元测试

一个独立的 .NET 控制台工程，直接链接 `src/EngCand/Core/` 与 `Contracts.cs`，不依赖 WinForms 主程序：

```powershell
dotnet run --project tests/wordbuf/EngCandCoreTests.csproj
```

覆盖：

- `WordBuffer`：字母/撇号/连字符、空格提交、退格（含跨词回溯）、句首标点、数字/小键盘、注入键与 KeyUp 忽略、Reset；
- `KeyboardHook`：安装/重复安装/卸载幂等（不实际注入按键）；
- `TextInjector`：`INPUT` 结构体大小与 API 签名检查；
- `SogouStateObserver`：伪造 `SoPY_Comp` 可见窗口验证组词状态与 Changed 事件。

## 程序自带自检

```powershell
dist\app\EngCand.exe --selftest
# exit code 0 = PASS；结果写到 exe 同目录与 %APPDATA%\EngCand\selftest.log
```

## 开发诊断模式

```powershell
dist\app\EngCand.exe --detect-test   # 自动输入中文/英文，验证搜狗模式识别
dist\app\EngCand.exe --caret-test    # 打印当前前台窗口的插入符/兜底锚点
```

> 注意：`--detect-test` 会短暂占据前台窗口并模拟按键，仅供开发调试。GUI 端到端测试脚本与构建机环境相关，未纳入仓库。
