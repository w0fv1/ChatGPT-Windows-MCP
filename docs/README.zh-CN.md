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
启动
```

程序负责后续的依赖检查、Windows-MCP 启动、Tunnel Profile 生成、诊断和 OpenAI Control Plane 连接。

## 功能

- WPF + XAML Windows 桌面界面。
- Tunnel ID / Runtime API Key 两步向导。
- 一键启动和停止。
- 自动管理 Windows-MCP 和官方 `tunnel-client`。
- 以实际 `tunnel metadata fetched` 为 Tunnel 就绪条件，避免进程存活造成误判。
- 支持 Windows 系统代理自动检测和 Control Plane 手动代理。
- 高级选项中提供日志、连接诊断和运行参数。
- Windows x64 自包含单文件发布。
- GitHub Actions 构建、CodeQL、安全策略、Dependabot 和自动 Release。

## 快速开始

1. 在 GitHub Releases 下载 `ChatGPT-Windows-MCP-win-x64.zip`。
2. 解压后运行 `ChatGPT-Windows-MCP.exe`。
3. 点击 **创建链接**。
4. 第一步按照提示创建 OpenAI Tunnel，并填入 `tunnel_...`。
5. 第二步创建 Runtime API Key 并填入。
6. 点击 **完成**，回到主页点击 **启动**。
7. Tunnel 显示已连接后，在 ChatGPT 中使用对应连接器。

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
