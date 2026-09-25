# SLDataAPI

> ## ⚠️ 安全警告
>
> 服务端链路为**明文**：HTTP（默认 8081）、控制/语音 WebSocket（`ws://`，语音默认 8082）。中间节点可窃听凭据与语音；凭据泄露等同服务器被控。
>
> 防火墙、反向代理（HTTPS/WSS）、凭据强度与保管由**使用者自行负责**；相关后果本项目与作者不承担责任。建议仅内网使用，对外必须走加密代理。详见 Wiki [Security-Model](https://github.com/DNTOF/SLDataAPI/wiki/Security-Model)。

SCP:SL 专用服务器的 **LabAPI 原生插件**（v2.4 起，非 EXILED）。对外提供 HTTP 数据查询、远程控制、控制 WebSocket 事件流、语音转发与录音取证。

| | |
|---|---|
| **当前版本** | **2.6.0 PEAK** · [Release](https://github.com/DNTOF/SLDataAPI/releases/tag/v2.6.0_PEAK) |
| **文档** | [Wiki](https://github.com/DNTOF/SLDataAPI/wiki) · 现行接口 [HTTP-API](https://github.com/DNTOF/SLDataAPI/wiki/HTTP-API) · 2.5.x 旧接口 [Old-HTTP-API](https://github.com/DNTOF/SLDataAPI/wiki/Old-HTTP-API) |
| **反馈** | [Issues](https://github.com/DNTOF/SLDataAPI/issues)（大版本升级后遇到问题请积极反馈） |

---

## 能力概览

| 能力 | 说明 |
|------|------|
| 数据查询 | `GET /get_sl_data`：人数、回合、核弹、玩家（SteamID/坐标）、DNT_OF 插件状态；`GET /plugins/adapted` + `adapted_plugins` 适配插件发现 |
| 控制接口 | `/control/*`：玩家 / 管理 / 回合 / 地图 / CASSIE / 全服广播 / 管理聊天 / 控制台 / 插件 / 文件 / 日志 / 举报等；HTTP 或 WS 二选一 |
| 事件流 | 控制 WS 订阅：回合、进出、死亡、电梯、门等 |
| 语音转发 | 独立端口 WS，全频道 48kHz PCM（SPY） |
| 语音录音 | 分轨 WAV + 时间轴 TSV，按局保留；可选 WebDAV（仅 https）自动上传定稿 zip |
| 举报 | Esc 服务器设置面板 + `/control/reports`（默认关） |
| 审计 | 侵入性控制操作写入 `control_log.json`（默认开） |

从 EXILED 迁移：DLL 放 `LabAPI/plugins/global/`；配置在 `LabAPI/configs/<端口>/SLDataAPI/config.yml`；启停用 `properties.yml` 或 `/control/plugins`。同服 EXILED 插件仍可通过反射桥探测 / 控制。

---

## ⚠️ 运行依赖（重点）

插件运行时需要两个程序集，**游戏本身不自带**（`SCPSL_Data/Managed/` 里没有），由 LabAPI 的依赖目录提供。**手动安装或精简安装 LabAPI 的服务器很可能缺失**，缺失时插件会加载失败或启动即报错。

| 依赖 | 版本 | 缺失现象 |
|------|------|----------|
| `0Harmony.dll` | 2.3.x（建议与 LabAPI 自带一致） | 插件加载失败 / 事件补丁不生效 |
| `Newtonsoft.Json.dll` | 13.0.x（程序集版本 13.0.0.0） | 插件加载失败 / 启动即崩 |

**放置位置**（所有端口共享的全局依赖目录）：

```
%AppData%/SCP Secret Laboratory/LabAPI/dependencies/global/0Harmony.dll
%AppData%/SCP Secret Laboratory/LabAPI/dependencies/global/Newtonsoft.Json.dll
```

**获取渠道：**

- `0Harmony.dll`：Harmony 官方仓库 https://github.com/pardeike/Harmony/releases（选择 **net472** 版本，文件名为 `0Harmony.dll`）
- `Newtonsoft.Json.dll`：NuGet https://www.nuget.org/packages/Newtonsoft.Json（13.0.x）
- 或从已完整安装 LabAPI 的其他服务器 `LabAPI/dependencies/global/` 直接复制

放入后重启服务器生效。

---

## 安装 / 升级

1. 从 [Release](https://github.com/DNTOF/SLDataAPI/releases/tag/v2.6.0_PEAK) 下载 `SLDataAPI.dll`（或自行 `dotnet build -c Release`，见 [Building](https://github.com/DNTOF/SLDataAPI/wiki/Building)）
2. 确认上方两个依赖已就位
3. 升级前备份 `config.yml` 与 `apikey.config`
4. 将 `SLDataAPI.dll` 放入 `%AppData%/SCP Secret Laboratory/LabAPI/plugins/global/`
5. 启动服务器生成配置，**把 `verify_token` 改成强随机值**后重启
6. 需要控制面时：本地控制台执行 `sldataapi apikey create <id> admin`（或 `duty`）
7. 防火墙按需放行 `http_port`（及启用时的 `voice_port`）

从 2.5.x 升级：控制端改用 API Key、路径改为 RA 对齐分组，对照表见 Wiki [HTTP-API](https://github.com/DNTOF/SLDataAPI/wiki/HTTP-API)。

---

## 配置（摘要）

路径：`LabAPI/configs/<端口或 global>/SLDataAPI/config.yml`（键名 **snake_case**）。

```yaml
debug: false
verify_token: "your_secret_token"   # 必须改成强随机值；默认/弱口令会 fail-closed 关闭数据口
http_port: 8081
push_interval_seconds: 8

control_enabled: false              # 关则所有 /control/* → 404
control_transport: http             # http | ws（硬互斥）
# control_token 已废弃：写了也会被忽略并警告

auto_update_check: true             # 启动 + 默认 72h 静默复查 GitHub Releases
auto_update_install: true           # 仅稳定正式包；未强签名构建拒绝自动安装
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

完整字段、YAML 坑、token 写法见 Wiki [Configuration](https://github.com/DNTOF/SLDataAPI/wiki/Configuration)。

---

## 鉴权

| 通道 | 凭据 | 配置位置 |
|------|------|----------|
| `GET /get_sl_data`、`GET /plugins/adapted` | `verify_token`（`Authorization: Bearer` / `X-SLDataAPI-Token`；`?token=` 兼容但已弃用） | `config.yml` |
| `/control/*`、控制 WS、语音口 | **API Key** | `apikey.config`（同配置目录，仅存指纹） |

控制面请求头（二选一）：

```http
Authorization: Bearer <api_key>
X-SLDataAPI-Key: <api_key>
```

不再接受：`X-Control-Token`、控制面 URL `?token=` / `?key=`。

### 管理 API Key（仅本地控制台）

```text
sldataapi apikey create <id> <duty|admin> [note]
sldataapi apikey list
sldataapi apikey revoke <id>
```

- `sldataapi` / `slda` 经远程控制通道（HTTP 与 WS 的 `/control/console/command`）一律拒绝；本地控制台执行立即生效。
- `create` 成功后明文写入配置目录的 `apikey_once_<id>.txt`，命令只回文件路径，不回密钥；文件约 **5 分钟后自动删除**，请尽快存进密码管理器。
- 丢失明文无法找回，只能 `revoke` 后重新 `create`。
- `duty` 偏只读，默认不授予 `/control/audit/list`、广播、管理聊天；`admin` 按端点目录授权，控制台 / 插件 / 文件等需 `endpoints_override` 单独放开。
- 若用权限插件收窄 RemoteAdmin，请同时拒绝 `sldataapi` / `slda`。

---

## 接口入口

| 类型 | 地址 |
|------|------|
| 数据 | `GET http://<host>:8081/get_sl_data` + `Authorization: Bearer <verify_token>` |
| 控制 HTTP | `POST http://<host>:8081/control/...` + API Key 头 |
| 控制 WS | `ws://<host>:8081/control` + 握手 API Key 头（`control_transport: ws`） |
| 语音 | `ws://<host>:8082/ws` · `GET :8082/status` + API Key |

全服广播示例：

```http
POST /control/broadcast
Authorization: Bearer <admin_key>
Content-Type: application/json

{"message":"服务器将于 5 分钟后重启","duration_seconds":10}
```

完整端点表与 curl 示例：[HTTP-API](https://github.com/DNTOF/SLDataAPI/wiki/HTTP-API)。WS 协议与语音帧：[WS-Control-Protocol](https://github.com/DNTOF/SLDataAPI/wiki/WS-Control-Protocol) · [Voice-Forwarding](https://github.com/DNTOF/SLDataAPI/wiki/Voice-Forwarding)。

仍为 501 占位：`/control/player/inventory`、`/control/dummies`。

---

## AstrBot

插件：[astrbot_plugin_sl_query](https://github.com/DNTOF/astrbot_plugin_sl_query)。`/bindlab <IP> <verify_token>` 后 `/sl` 显示 `[LAB]` 标记。

---

## 开发

[Development-Guide](https://github.com/DNTOF/SLDataAPI/wiki/Development-Guide) · [Architecture](https://github.com/DNTOF/SLDataAPI/wiki/Architecture) · [Building](https://github.com/DNTOF/SLDataAPI/wiki/Building)

开发 skill 与冒烟脚本：Release 附件 `SLDataAPI-DevKit-v2.6.0_PEAK.zip`。

---

## 支持

- QQ 群：984840871
- Issues：https://github.com/DNTOF/SLDataAPI/issues

## 许可证

[GPLv3](LICENSE)
