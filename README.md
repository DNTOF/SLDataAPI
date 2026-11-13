<div align="center">

# SLDataAPI

**把你的 SCP:SL 服务器变成一个可编程的平台**

LabAPI 原生插件 · 数据查询 · 远程控制 · 实时事件流 · 语音转发与录音取证

[![Release](https://img.shields.io/github/v/release/DNTOF/SLDataAPI?style=flat-square&label=release&color=2ea44f)](https://github.com/DNTOF/SLDataAPI/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/DNTOF/SLDataAPI/total?style=flat-square&color=blue)](https://github.com/DNTOF/SLDataAPI/releases)
[![Tests](https://img.shields.io/github/actions/workflow/status/DNTOF/SLDataAPI/functional-tests.yml?branch=main&style=flat-square&label=tests)](https://github.com/DNTOF/SLDataAPI/actions/workflows/functional-tests.yml)
[![License](https://img.shields.io/badge/license-Apache--2.0-orange?style=flat-square)](LICENSE)
<br>
[![SCP:SL](https://img.shields.io/badge/SCP%3ASL-14.2.8-c0392b?style=flat-square)](https://scpslgame.com/)
[![LabAPI](https://img.shields.io/badge/LabAPI-native-8e44ad?style=flat-square)](https://github.com/northwood-studios/LabAPI)
[![.NET Framework](https://img.shields.io/badge/.NET%20Framework-4.8-512bd4?style=flat-square)](https://dotnet.microsoft.com/)
![QQ 群](https://img.shields.io/badge/QQ%20群-984840871-12b7f5?style=flat-square)

[快速开始](#-快速开始) · [功能一览](#-功能一览) · [配置](#-配置) · [鉴权与安全](#-鉴权与安全) · [接口](#-接口速览) · [Wiki](https://github.com/DNTOF/SLDataAPI/wiki) · [Q&A](https://github.com/DNTOF/SLDataAPI/wiki/Q%26A)

</div>

<br>

<div align="center">

| **40+** | **19** | **48 kHz** | **1** | **180+** |
|:---:|:---:|:---:|:---:|:---:|
| 控制端点 | 实时事件类型 | 全频道语音转发 | 端口同时承载 HTTP / WS / TLS | 自动化回归用例 |

</div>

## ✨ 为什么选择 SLDataAPI

<table>
<tr>
<td width="33%" valign="top">

### 📊 数据一手掌握
一个请求拿到人数、回合、核弹、玩家 SteamID 与坐标、游戏版本、当前节日，机器人和面板直接用。

</td>
<td width="33%" valign="top">

### 🎮 远程全面掌控
玩家、物品栏、回合、地图、CASSIE、封禁、控制台、文件……40+ 端点覆盖日常运维，HTTP 或 WebSocket 任选。

</td>
<td width="33%" valign="top">

### ⚡ 事件实时推送
回合、进出、死亡、逃脱、核弹、重生波次、SCP 收容……19 种游戏事件经 WebSocket 实时推送。

</td>
</tr>
<tr>
<td width="33%" valign="top">

### 🎙️ 语音转发与取证
全频道 48 kHz PCM 实时转发；分轨 WAV + 时间轴 TSV 按局归档，可自动上传到 WebDAV。

</td>
<td width="33%" valign="top">

### 🔐 安全默认开启
API Key 分级授权、仅存指纹；弱口令 fail-closed；同端口自签 TLS + 指纹固定；侵入性操作全程审计。

</td>
<td width="33%" valign="top">

### 🧩 开放可扩展
其它插件可注册为「适配插件」，通过 SLDataAPI 对外暴露状态与操作，无需自建 HTTP 服务。

</td>
</tr>
</table>

## 📦 功能一览

| 模块 | 能力 |
|------|------|
| **数据查询** | `GET /get_sl_data`：人数、回合、核弹、玩家（SteamID / 坐标）、`game_version` / `active_holiday`、DNT_OF 插件状态；`GET /plugins/adapted` 发现适配插件 |
| **控制面** | `/control/*`：玩家 / 物品栏 / 管理 / 回合 / 地图（设施状态快照、灯光颜色、布局导出）/ CASSIE / 全服广播 / 管理聊天 / 控制台 / 插件 / 文件 / 日志 / 举报 / 假人；`/control/player/data` 含生命、护盾、体力、物品、弹药、当前效果与 TemporaryBypass 开门权限 |
| **事件流** | 控制 WS 订阅：回合开始 / 结束、进出、死亡、角色变化、逃脱、封禁 / 踢出、重生波次、核弹、去污、发电机、SCP 收容播报、举报等 |
| **节日玩法** | 万圣节糖果 / 棉花糖人 / 节日物品：跟随游戏当前节日自动启用，未开启时返回结构化 `409 holiday_inactive`，无需改代码 |
| **语音转发** | 独立端口 WebSocket，全频道 48 kHz PCM（SPY） |
| **语音录音** | 分轨 WAV + 时间轴 TSV，按局保留；可选 WebDAV（仅 https）自动上传定稿 zip |
| **举报** | Esc 服务器设置面板 + `/control/reports`（默认关） |
| **审计** | 侵入性控制操作写入 `control_log.json`（默认开） |
| **自动更新** | 启动时 + 每 72 小时静默检查 GitHub Releases；仅安装经强名称验签的正式包 |

## 🚀 快速开始

### 1. 准备依赖

插件运行时需要两个程序集，放入所有端口共享的全局依赖目录：

```text
%AppData%/SCP Secret Laboratory/LabAPI/dependencies/global/0Harmony.dll
%AppData%/SCP Secret Laboratory/LabAPI/dependencies/global/Newtonsoft.Json.dll
```

| 依赖 | 版本 | 获取 | 缺失现象 |
|------|------|------|----------|
| `0Harmony.dll` | 2.3.x（建议与 LabAPI 自带一致） | [Harmony Releases](https://github.com/pardeike/Harmony/releases)（选 **net472**） | 插件加载失败 / 事件补丁不生效 |
| `Newtonsoft.Json.dll` | 13.0.x（程序集版本 13.0.0.0） | [NuGet](https://www.nuget.org/packages/Newtonsoft.Json) | 插件加载失败 / 启动即崩 |

### 2. 安装插件

1. 从 [**最新 Release**](https://github.com/DNTOF/SLDataAPI/releases/latest) 下载 `SLDataAPI.dll`（或自行构建，见 [Building](https://github.com/DNTOF/SLDataAPI/wiki/Building)）
2. 放入 `%AppData%/SCP Secret Laboratory/LabAPI/plugins/global/`
3. 启动服务器，自动生成配置文件

### 3. 完成初始化

1. **把 `verify_token` 改成强随机值**，然后重启
2. 需要控制面时，在本地控制台执行 `sldataapi apikey create <id> admin`（或 `duty`）
3. 防火墙按需放行 `http_port`（启用语音时还有 `voice_port`）

### 4. 发出第一个请求

```bash
curl -H "Authorization: Bearer <verify_token>" http://<host>:8081/get_sl_data
```

> [!TIP]
> **升级前**请备份 `config.yml` 与 `apikey.config`。从旧版 `control_token` 鉴权升级时，控制端需改用 API Key，路径改为 RA 对齐分组，对照表见 Wiki [HTTP-API](https://github.com/DNTOF/SLDataAPI/wiki/HTTP-API)。

## 🔧 配置

路径：`LabAPI/configs/<端口或 global>/SLDataAPI/config.yml`（键名 **snake_case**）。

<details>
<summary><b>展开查看常用配置项</b></summary>

```yaml
debug: false
verify_token: "your_secret_token"   # 必须改成强随机值；默认/弱口令会 fail-closed 关闭数据口
http_port: 8081
push_interval_seconds: 8
tls_mode: optional                  # off | optional | required：同端口自签 TLS

control_enabled: false              # 关则所有 /control/* → 404
control_transport: http             # http | ws（硬互斥）
# control_token 已废弃：写了也会被忽略并警告

auto_update_check: true             # 启动 + 默认 72h 静默复查 GitHub Releases
auto_update_install: true           # 仅稳定正式包；入站须正式强签名验签；未签名当前可引导升级
# auto_update_check_interval_hours: 72

voice_enabled: false
voice_port: 8082
voice_record_enabled: false

webdav_upload_enabled: false        # 定稿 zip 自动上传（默认关）
webdav_url: ""                      # 仅 https:// 目录 URL，或含 {filename} 的模板
webdav_username: ""
webdav_password: ""

report_enabled: false
control_log_enabled: true
# apikey_copy_to_clipboard: false   # Windows 创建 Key 后是否复制剪贴板（默认关）
```

</details>

完整字段、YAML 常见坑、token 写法见 Wiki [Configuration](https://github.com/DNTOF/SLDataAPI/wiki/Configuration)。

## 🔐 鉴权与安全

| 通道 | 凭据 | 存放位置 |
|------|------|----------|
| `GET /get_sl_data`、`GET /plugins/adapted` | `verify_token`（`Authorization: Bearer` / `X-SLDataAPI-Token`；`?token=` 兼容但已弃用） | `config.yml` |
| `/control/*`、控制 WS、语音口 | **API Key** | `apikey.config`（同配置目录，仅存指纹） |

控制面请求头（二选一）。`X-Control-Token`、控制面 URL 中的 `?token=` / `?key=` 均**不再接受**。

```http
Authorization: Bearer <api_key>
X-SLDataAPI-Key: <api_key>
```

### API Key 管理

```text
sldataapi apikey create <id> <duty|admin> [note]
sldataapi apikey list
sldataapi apikey revoke <id>
```

- **仅限本地控制台（LocalAdmin）**：RemoteAdmin 与远程控制通道一律拒绝（与 `cert` 一致）；`sldataapi` / `slda` 经 `/control/console/command`（HTTP 与 WS）同样拒绝。
- **一次性交付**：`create` 成功后明文写入配置目录的 `apikey_once_<id>.txt`，命令只返回文件路径，不回显密钥；文件约 **5 分钟后自动删除**，请尽快存入密码管理器。
- **不可找回**：丢失明文只能 `revoke` 后重新 `create`。
- **分级授权**：`duty` 偏只读，默认不授予 `/control/audit/list`、广播、管理聊天；`admin` 按端点目录授权，控制台 / 插件 / 文件等需通过 `endpoints_override` 单独放开。
- 权限插件收窄 RemoteAdmin 时，仍建议同时拒绝 `sldataapi` / `slda` 的其它子命令。

### 同端口自签 TLS

| `tls_mode` | 行为 |
|------------|------|
| `optional`（默认） | 主端口与语音端口**同端口**同时接受 TLS 与明文：首字节为 TLS 握手（`0x16`）走 TLS 1.2，否则按明文处理 |
| `required` | 明文 HTTP、明文控制 WS / 语音 WS 一律返回 `426`，`data.code = "tls_required"` |
| `off` | 不生成 / 加载证书，行为与旧版一致 |

- 首次启动在配置目录生成 `sld_tls.pfx` 与随机口令文件 `sld_tls.pwd`（RSA-2048 / SHA-256 / 约 10 年，`CN=SLDataAPI`）。Windows 上文件权限暂未收紧，请自行限制配置目录访问。
- 启动日志打印 `TLS: <mode>, SHA-256 <指纹>`，客户端应**固定（pin）该指纹**，而不是信任 CA。
- 控制 WS 与语音 WS 的 `hello` 消息带有 `tls`（本连接是否加密）与 `cert_fingerprint`（`tls_mode: off` 时为 `null`）。

```text
sldataapi cert show     # 查看模式 / 指纹 / 有效期
sldataapi cert regen    # 重新生成并热替换（仅对新连接生效；已 pin 的客户端须重新确认指纹）
```

`cert` 子命令同样**仅限本地控制台**，`regen` 会写入控制审计日志。

## 🔌 接口速览

| 类型 | 地址 |
|------|------|
| 数据 | `GET http://<host>:8081/get_sl_data` + `Authorization: Bearer <verify_token>` |
| 控制 HTTP | `POST http://<host>:8081/control/...` + API Key 头 |
| 控制 WS | `ws://<host>:8081/control` + 握手 API Key 头（`control_transport: ws`） |
| 语音 | `ws://<host>:8082/ws` · `GET :8082/status` + API Key |
| TLS | 以上均可同端口改用 `https://` / `wss://`（`tls_mode` 非 `off`；自签证书，按指纹固定） |

<details>
<summary><b>示例：全服广播</b></summary>

```http
POST /control/broadcast
Authorization: Bearer <admin_key>
Content-Type: application/json

{"message":"服务器将于 5 分钟后重启","duration_seconds":10}
```

</details>

<details>
<summary><b>示例：假人管理 <code>/control/dummies</code>（admin）</b></summary>

```http
POST /control/dummies
Authorization: Bearer <admin_key>
Content-Type: application/json

{"action":"spawn","nickname":"Bot","role":"ClassD"}
```

`action` 可选 `spawn` | `list` | `remove`；同时最多 5 个；`remove` 的 `target` 为假人 PlayerId / 昵称，或 `all`。

</details>

📚 完整文档：[HTTP-API](https://github.com/DNTOF/SLDataAPI/wiki/HTTP-API)（端点表与 curl 示例）· [WS-Control-Protocol](https://github.com/DNTOF/SLDataAPI/wiki/WS-Control-Protocol) · [Voice-Forwarding](https://github.com/DNTOF/SLDataAPI/wiki/Voice-Forwarding)

## 🌐 生态

| 项目 | 说明 |
|------|------|
| [astrbot_plugin_sl_query](https://github.com/DNTOF/astrbot_plugin_sl_query) | AstrBot 插件：`/bindlab <IP> <verify_token>` 后，`/sl` 显示 `[LAB]` 标记 |
| [AdaptedPluginSample](examples/AdaptedPluginSample) | 适配插件示例：把你自己的插件状态与操作挂到 SLDataAPI 上 |

## 🛠️ 开发

- 文档：[Development-Guide](https://github.com/DNTOF/SLDataAPI/wiki/Development-Guide) · [Architecture](https://github.com/DNTOF/SLDataAPI/wiki/Architecture) · [Building](https://github.com/DNTOF/SLDataAPI/wiki/Building)
- 开发 skill 与冒烟脚本：[最新 Release](https://github.com/DNTOF/SLDataAPI/releases/latest) 附件 `SLDataAPI-DevKit-v<版本>_<代号>.zip`

### 逻辑回归测试

`tests/` 下有四套独立的 net8.0 xUnit 项目，直接编译对应的纯逻辑源文件，**不引用**主工程 `SLDataAPI.csproj`，因此不需要游戏程序集、LabAPI 或 Unity，也不会编译、签名或打包 `SLDataAPI.dll`。

| 套件 | 覆盖范围 |
|------|----------|
| `SLDataAPI.Auth.Tests` | 鉴权、API Key 交付、端点 ACL、路径沙箱、TLS 策略、WS 调用闸门 |
| `SLDataAPI.Features.Tests` | 节日闸门、端点入参规则、玩家查找、反射查找 |
| `SLDataAPI.Update.Tests` | 更新检查与强名称判定 |
| `SLDataAPI.WebDav.Tests` | WebDAV 上传规则 |

```bash
dotnet test tests/SLDataAPI.Auth.Tests/SLDataAPI.Auth.Tests.csproj -c Release
dotnet test tests/SLDataAPI.Features.Tests/SLDataAPI.Features.Tests.csproj -c Release
dotnet test tests/SLDataAPI.Update.Tests/SLDataAPI.Update.Tests.csproj -c Release
dotnet test tests/SLDataAPI.WebDav.Tests/SLDataAPI.WebDav.Tests.csproj -c Release
```

> [!NOTE]
> 这些测试只做逻辑回归，不能代替进服集成，也不能说明完整插件在 SCP:SL 里可用。官方插件的编译与发布仍在本机完成。

<details>
<summary><b>CI 说明</b></summary>

推送、Pull Request 和手动 `workflow_dispatch` 会触发 `.github/workflows/functional-tests.yml`：在 GitHub 托管的 `ubuntu-24.04` 上分别运行四套测试。任一失败会使该检查失败，其余套件仍会跑完；控制台日志和作业摘要中有每套的通过 / 失败 / 跳过数量。

仓库的 Actions 策略只允许 DNTOF 拥有的 action，所以工作流用 git 检出、官方 `dotnet-install.sh` 安装 .NET 8，不调用其他组织的 action。可下载的 TRX 制品目前不可用，要等所有者允许官方的 artifact action 之后才能保留。

</details>

## 💬 支持

- 💭 QQ 群：**984840871**
- 🐛 问题反馈：[GitHub Issues](https://github.com/DNTOF/SLDataAPI/issues)
- ❓ 常见问题：[Wiki Q&A](https://github.com/DNTOF/SLDataAPI/wiki/Q%26A)
- ❤️ 赞助：[爱发电](https://afdian.com/a/DNT_OF)

## 📄 许可证

Copyright (C) 2026 DNT_OF · 以 [Apache-2.0](LICENSE) 授权，详见 [NOTICE](NOTICE)。

本项目已由 **GPL-3.0-only** 变更为 **Apache-2.0**：在遵守 Apache-2.0 与 NOTICE 的前提下允许闭源使用与再分发，不再要求衍生作品强制开源。

复用或改写本项目代码（尤其语音流相关实现）时，须：

1. 保留源文件版权 / SPDX 头与本仓库 [NOTICE](NOTICE)；
2. 注明来自 [DNTOF/SLDataAPI](https://github.com/DNTOF/SLDataAPI)；
3. 禁止删除或篡改版权与来源信息后重新发布（含「删名字 + 换皮当自己项目」）。

<div align="center">
<br>

**如果 SLDataAPI 对你有帮助，欢迎点一个 ⭐ Star！**

</div>
