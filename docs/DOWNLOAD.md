# 下载免构建包（Windows x64）

## 方式一：GitHub Actions 构建产物（推荐，云端自动构建）

每次推送代码后，GitHub 会在云端构建并生成 EngCand-win64 产物：

**[https://github.com/LiHua8930/engcand/actions](https://github.com/LiHua8930/engcand/actions)**

1. 打开上面链接 → 点最新一次成功的 **build** 运行；
2. 页面底部 **Artifacts** → 下载 EngCand-win64（zip）；
3. 解压 → 双击 启动.bat 即可运行（需要 .NET 10 Desktop Runtime）。

> CI 会从公开语料现场生成词典（约几十 MB 下载），构建一次约 2~5 分钟。

## 方式二：手动从 base64 分片还原官方包（无需 GitHub 登录）

本目录 `release-assets/` 下的 `engcand-v1.0.1-win64.zip.b64.partNN` 是官方
v1.0.1 免构建包（与构建机产物一致）的 base64 分片。用 PowerShell 还原：

```powershell
cd docs/release-assets
$parts = Get-ChildItem *.b64.part* | Sort-Object Name
$b64 = (-join ($parts | ForEach-Object { Get-Content -Raw $_ }))
[IO.File]::WriteAllBytes('../../EngCand-v1.0.1-win64.zip', [Convert]::FromBase64String($b64))
Get-FileHash ../../EngCand-v1.0.1-win64.zip -Algorithm SHA256
# 期望：FB36D510BF5F7AC91AFDFF3299893C0B41A2637A7AB0FD018266DB61F5E4E11E
```

解压后双击 `启动.bat`。
## 常见问题

- **提示"已经在运行"**：程序是单实例的，看托盘图标（可能在 ^ 隐藏区）；重启前先在托盘菜单退出。
- **需要 .NET 10 Desktop Runtime**：https://dotnet.microsoft.com/download/dotnet/10.0 （选 Desktop Runtime x64）
- **中文模式识别**：当前按搜狗拼音实现；其它输入法可在托盘切「强制英文」。