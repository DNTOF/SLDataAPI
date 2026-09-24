# SLDataAPI

> ## ⚠️ 安全警告
>
> 服务端链路为**明文**：HTTP（默认 8081）、控制/语音 WebSocket（`ws://`，语音默认 8082）。中间节点可窃听凭据与语音；凭据泄露等同服务器被控。
>
> 防火墙、反向代理（HTTPS/WSS）、凭据强度与保管由**使用者自行负责**；相关后果本项目与作者不承担责任。建议仅内网使用，对外必须走加密代理。详见 Wiki [[Security-Model]](https://github.com/DNTOF/SLDataAPI/wiki/Security-Model)。

**版本：** 2.6.0-preview-DevOnly（Kerckhoffs） · **LabAPI 原生插件**（v2.4 起，非 EXILED）  
**依赖：** LabAPI（游戏自带）· `0Harmony` 2.3.x · `Newtonsoft.Json` 13.0.x（后两者须放在 `LabAPI/dependencies/global/`，缺失会加载失败）

📚 **接口与开发文档：** https://github.com/DNTOF/SLDataAPI/wiki  
- 稳定线 `main`（2.5.x）：[[HTTP-API]](https://github.com/DNTOF/SLDataAPI/wiki/HTTP-API) · `control_token`  
- 预览线 `preview/v2.6.0-DevOnly`（2.6）：[[Preview-HTTP-API]](https://github.com/DNTOF/SLDataAPI/wiki/Preview-HTTP-API) · API Key + RA 对齐路径

---

## 能力概览

| 能力 | 说明 |
|------|------|
| 数据查询 | `GET /get_sl_data`；适配发现：`GET /plugins/adapted` + `adapted_plugins`（`verify_token`，与 2.5.5 Nexus 对齐）：人数、回合、核弹、玩家（SteamID/坐标）、DNT_OF 插件状态 |
| 控制接口 | `/control/*`：玩家/回合/地图/CASSIE/控制台/插件/文件/日志/举报等；HTTP 或 WS 二选一 |
| 事件流 | 控制 WS 订阅：回合、进出、死亡、电梯、门等（v2.5.4+） |
| 语音转发 | 独立端口 WS，全频道 48kHz PCM（SPY） |
| 语音录音 | 分轨 WAV + 时间轴 TSV，按局保留（v2.5+） |
| 举报 | Esc 服务器设置面板 + `/control/reports`（默认关） |
| 审计 | 侵入性控制操作写入 `control_log.json`（默认开） |

从 EXILED 迁移：DLL 放 `LabAPI/plugins/global/`；配置 `LabAPI/configs/<端口>/SLDataAPI/config.yml`；启停用 `properties.yml` 或 `/control/plugins`。同服 EXILED 插件仍可通过反射桥探测/控制。

---

## 运行依赖

| 文件 | 版本 | 路径 |
|------|------|------|
| `0Harmony.dll` | 2.3.x | `%AppData%/SCP Secret Laboratory/LabAPI/dependencies/global/` |
| `Newtonsoft.Json.dll` | 13.0.x | 同上 |

编译需本机 SCP:SL 专用服务器与 LabAPI 目录（`SCPSL_DIR` / `LABAPI_DIR`，见 Wiki [[Building]](https://github.com/DNTOF/SLDataAPI/wiki/Building)）。

---

## 安装

```bash
dotnet build -c Release
```

1. 确认上述依赖 DLL 已就位  
2. 复制 `bin/Release/net48/SLDataAPI.dll` → `LabAPI/plugins/global/`  
3. 启动服务器，编辑生成的 `config.yml`，重启生效  
4. 防火墙放行 `http_port`（及 `voice_port` 若启用）

---

## 配置（摘要）

路径：`LabAPI/configs/<端口或 global>/SLDataAPI/config.yml`（键名 **snake_case**）

```yaml
debug: false
verify_token: "your_secret_token"   # 只读 /get_sl_data
http_port: 8081
push_interval_seconds: 8

control_enabled: false              # 关则所有 /control/* → 404
control_transport: http               # http | ws（硬互斥）
# control_token 已废弃（2.6 忽略并打警告）

voice_enabled: false
voice_port: 8082
voice_record_enabled: false
report_enabled: false
control_log_enabled: true
```

完整字段、YAML 坑、token 写法见 Wiki [[Configuration]](https://github.com/DNTOF/SLDataAPI/wiki/Configuration)。

---

## 鉴权（2.6 预览）

| 通道 | 凭据 | 配置位置 |
|------|------|----------|
| `GET /get_sl_data` | `verify_token`（`?token=`，与 2.5 相同） | `config.yml` |
| `/control/*`、控制 WS、语音口 | **API Key**（明文写入一次性 txt，命令只回路径） | `apikey.config`（同配置目录） |

控制面请求头（二选一）：

```http
Authorization: Bearer <api_key>
X-SLDataAPI-Key: <api_key>
```

已移除：`X-Control-Token`、控制面 URL `?token=` / `?key=`。

**本地管理 Key**（不可经远程 `/control/console/command` 执行）：

```text
sldataapi apikey create <id> <duty|admin> [note]
sldataapi apikey list
sldataapi apikey revoke <id>
```

`sldataapi` / `slda` 管理 CLI 已被远程控制通道（HTTP + WS 的 `/control/console/command`）硬拒绝；本地控制台（LocalAdmin / RemoteAdmin / 游戏内控制台）执行 `create` / `revoke` 会立即生效，不再弹出确认窗口或 `[y/N]` 提示。`create` 成功后明文写入配置目录下 `apikey_once_<id>.txt`（同 id 覆盖上一份），命令 response **只回该路径**（不回密钥，避免进入 LocalAdmin 命令历史）；请复制到密码管理器后立即删除该文件。Windows 下会后台尽力复制到剪贴板（超时、失败均静默，不影响创建）。`duty` 偏只读；`admin` 按端点 catalog 授权，**不会**自动开放 catalog 为 `false` 的路径（控制台、插件、文件等），可用 `endpoints_override` 单独放开。

路径与 curl 示例：Wiki [[Preview-HTTP-API]](https://github.com/DNTOF/SLDataAPI/wiki/Preview-HTTP-API)。稳定 2.5 仍用 [[HTTP-API]](https://github.com/DNTOF/SLDataAPI/wiki/HTTP-API)。

---

## 接口入口

| 类型 | 地址 |
|------|------|
| 数据 | `GET http://<host>:8081/get_sl_data?token=<verify_token>` |
| 控制 HTTP | `POST http://<host>:8081/control/...` + API Key 头 |
| 控制 WS | `ws://<host>:8081/control` + 握手带 API Key 头（`control_transport: ws`） |
| 语音 | `ws://<host>:8082/ws` · `GET :8082/status` + API Key |

WS 协议、语音帧格式、错误码：[[WS-Control-Protocol]](https://github.com/DNTOF/SLDataAPI/wiki/WS-Control-Protocol) · [[Voice-Forwarding]](https://github.com/DNTOF/SLDataAPI/wiki/Voice-Forwarding)。

---

## AstrBot

插件：[astrbot_plugin_sl_query](https://github.com/DNTOF/astrbot_plugin_sl_query) — `/bindlab <IP> <verify_token>` 后 `/sl` 显示 `[LAB]` 标记。

---

## 开发

[[Development-Guide]](https://github.com/DNTOF/SLDataAPI/wiki/Development-Guide) · [[Architecture]](https://github.com/DNTOF/SLDataAPI/wiki/Architecture) · [[Building]](https://github.com/DNTOF/SLDataAPI/wiki/Building)

---

## 支持

- QQ 群：984840871  
- Issues：https://github.com/DNTOF/SLDataAPI/issues  

## 许可证

[GPLv3](LICENSE)
