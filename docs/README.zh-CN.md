# ChatGPT Windows MCP（中文）

一个面向 Windows 的桌面启动器，把 **Windows-MCP + OpenAI Secure MCP Tunnel** 的配置和运行流程做成简洁的图形界面。

> **非官方社区项目。** 本项目与 OpenAI 无隶属或官方背书关系；ChatGPT、OpenAI 等名称及商标归其各自权利人所有。

## 核心目标

普通用户不需要理解 MCP 本地端口、Tunnel Profile、Python 包和代理链路。正常流程只保留：

```text
创建 OpenAI Tunnel
      ↓
填写 Tunnel ID
      ↓
创建 Runtime API Key
      ↓
填写 API Key
      ↓
可选：配置 HTTP(S) 代理
      ↓
启动
```

程序负责后续的依赖检查、Windows-MCP 启动、Tunnel Profile 生成、诊断和 OpenAI Control Plane 连接。

## 功能

- WPF + XAML Windows 桌面界面。
- Tunnel ID → Runtime API Key → 网络代理 → ChatGPT 插件四步向导。
- 填写插件名字后，一键复制给 Plugin Creator 的创建提示词；自动带入 Tunnel ID，不包含 API Key。
- 一键启动和停止。
- 发布版内置完整运行环境，默认配置无需 WinGet、系统 Python 或下载依赖。未准备内置资源的开发构建保留 WinGet 和官方压缩包安装备用方案。
- 自动管理 Windows-MCP 和官方 `tunnel-client`。
- 以实际 `tunnel metadata fetched` 为 Tunnel 就绪条件，避免进程存活造成误判。
- 支持 Windows 系统代理自动检测和手动 HTTP(S) 代理；代理会用于 tunnel-client 下载、WinGet/uv、Windows-MCP 包解析和 Tunnel 连接。
- 高级选项中提供日志、连接诊断和运行参数。
- Windows x64 自包含单文件发布。
- GitHub Actions 构建、CodeQL、安全策略、Dependabot 和自动 Release。

## 快速开始

1. 在 GitHub Releases 选择下载：`ChatGPT-Windows-MCP-win-x64.zip` 为完整内置版；`ChatGPT-Windows-MCP-online-win-x64.zip` 为在线下载版，首次启动按需下载依赖。两种都包含 .NET，可直接运行启动器。
2. 解压后运行 `ChatGPT-Windows-MCP.exe`。
3. 点击 **创建链接**。
4. 第一步点击 **打开 Tunnel 创建页面** 或 **复制链接**，创建后填入 `tunnel_...`。
5. 第二步手动打开 Runtime API Key 页面或复制链接，创建后填入。向导切换和服务启动均不会自动打开网页。
6. 第三步先设置网络代理，无需代理时留空，点击 **下一步：配置插件**。
7. 进入第四步时自动准备依赖并启动 MCP 和 Tunnel，无需返回主页。填写插件名字，等待页面显示服务已启动。
   主页和插件页均在当前操作文案的右侧显示百分比，不显示图标或进度条。百分比按已完成的准备步骤更新，全部启动成功后达到 100%，不是下载字节百分比或计时估算。
8. 点击 **创建 MCP 应用**，在 ChatGPT 插件页点击 **添加 → 创建 MCP 应用**，连接选择 **隧道**，填入复制的 Tunnel ID。创建后，将连接详情页链接粘贴回启动器，点击 **复制创建提示词**，发送给 ChatGPT Work 中的 **@Plugin Creator**。点击 **完成配置** 回到主页时服务继续运行；主页的 **配置插件** 可直接返回此页。创建和使用插件期间保持启动器运行。

## 接入 ChatGPT

插件页会从连接详情链接中提取 `plugin_asdk_app...` ID，也支持直接填写该 ID。连接为可选项；未填写时仍可复制提示词，让 AI 尝试查找或创建 MCP 应用。AI 缺少 Tunnel 注册接口时会引导完成手动步骤。连接 ID 会保存，更换 Tunnel 后自动清除。

提示词要求绑定指定连接、更新同名占位插件，并通过该连接验证实际工具；禁止创建占位插件或将其他 Windows 工具的调用算作验证成功。启动器仅检查链接格式，连接是否对应指定 Tunnel 仍需在 ChatGPT 中确认。可使用 **打开 ChatGPT** 或 **复制 ChatGPT 链接** 进入对话。

“创建 MCP 应用”在未启动时会自动启动，启动失败后再次点击可以重试。它会检查当前 Tunnel、隧道就绪状态和本机 MCP 初始化及工具列表，再打开 ChatGPT。返回上一步修改代理前会停止服务，再次进入插件页时按保存的设置启动。“连接诊断”支持在服务运行中重新检查，无需停止服务；检查不会执行 Windows 工具。ChatGPT 的工作区关联、权限和注册结果仍需在 ChatGPT 中确认。

参考：[Tunnel 接入说明](https://developers.openai.com/api/docs/guides/secure-mcp-tunnels#connect-from-chatgpt)、[Plugin Creator 与连接绑定说明](https://developers.openai.com/plugins/build/plugins)。

## 工作目录与日志

`profiles` 用于保存 Tunnel 配置，`runtime` 用于健康检查地址文件。它们按需创建；启动器不再提前生成空目录，启动和停止时会清理空的工作目录，已有配置文件会保留。

`logs/app-YYYYMMDD.log` 实时追加程序会话、配置步骤、命令、进程 PID、标准输出/错误、退出码、耗时、健康状态变化以及完整异常堆栈和 HResult。Python 输出使用无缓冲模式；重新启动或诊断不会清空已有日志。API Key 和代理密码会脱敏。

## 安全提示

该程序连接的是能够操作本机 Windows 的 MCP 服务，必须把它当作高权限工具使用。

当前版本有几个重要安全特性：

- Windows-MCP 默认只监听 `127.0.0.1`。
- OpenAI Secure MCP Tunnel 从本机主动向外连接。
- 当前默认允许上游 Windows-MCP 暴露其完整工具集。
- 为了便携运行，Runtime API Key 当前仍以明文保存在本地 `config.json`。
- `config.json`、日志、profiles、runtime、下载工具和构建产物均被 Git 忽略。

请不要提交真实 `config.json`，也不要在公开 Issue 中粘贴未经清理的日志。

详细内容见 [SECURITY.md](../SECURITY.md)。

## 开发

需要 Windows 和 .NET 10 SDK：

```powershell
./scripts/dev-run.ps1
```

构建：

```powershell
dotnet build ./src/ChatGPTWindowsMcp/ChatGPTWindowsMcp.csproj -c Release -warnaserror
./scripts/publish.ps1
```

## 参与贡献

请阅读：

- [CONTRIBUTING.md](../CONTRIBUTING.md)
- [ROADMAP.md](../ROADMAP.md)
- [SECURITY.md](../SECURITY.md)
- [CODE_OF_CONDUCT.md](../CODE_OF_CONDUCT.md)

## 许可证

MIT License。

发布版 EXE 内置 uv/uvx 0.12.21、Python 3.13.14、Windows-MCP 0.8.5 及全部 92 个固定版本依赖包、Tunnel 客户端 v0.0.15 和配套 cloudflared。默认配置首次启动只进行本地释放与 SHA-256 校验，无需下载依赖；连接 OpenAI 仍需联网。手动指定其他 Python、Windows-MCP 或 Tunnel 版本时保留在线安装方式。发布脚本在构建时准备完整环境及许可证，复用校验通过的缓存。
