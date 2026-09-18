# SLDataAPI

> **安全警告：** 数据/控制/语音链路均为明文（HTTP · `ws://`）。中间节点可窃听凭据与语音；凭据泄露等同服务器被控。防火墙、反向代理（HTTPS/WSS）、token 保管由使用者负责，相关后果作者不承担责任。建议仅内网使用，对外必须加密代理。详见 [Security-Model](https://github.com/DNTOF/SLDataAPI/wiki/Security-Model)。

**版本：** 2.5.5（Nexus） · **LabAPI 原生插件**（v2.4 起，非 EXILED）  
**依赖：** LabAPI（游戏自带）· `0Harmony` 2.3.x · `Newtonsoft.Json` 13.0.x（后两者须放在 `LabAPI/dependencies/global/`）

完整接口与开发文档：[Wiki](https://github.com/DNTOF/SLDataAPI/wiki)  
- 本分支（稳定 2.5）：[HTTP-API](https://github.com/DNTOF/SLDataAPI/wiki/HTTP-API) · `verify_token` + `control_token`  
- 预览 2.6（`preview/v2.6.0-DevOnly`）：[Preview-HTTP-API](https://github.com/DNTOF/SLDataAPI/wiki/Preview-HTTP-API) · API Key

---

## 能做什么

| 能力 | 说明 |
|------|------|
| 数据查询 | `GET /get_sl_data`：人数、回合、核弹、玩家（SteamID/坐标）等 |
| 远程控制 | `/control/*`：玩家/回合/地图/CASSIE/控制台/插件/文件/日志/举报等；HTTP 或 WS 二选一 |
| 事件流 | 控制 WS 可订阅回合、进出、死亡、电梯、门等 |
| 语音转发 | 独立端口 WS，全频道 48kHz PCM（SPY） |
| 语音录音 | 分轨 WAV + 时间轴 TSV，按局保留 |
| 举报 / 审计 | Esc 面板举报；侵入性控制写入 `control_log.json` |
| 适配插件发现 | `GET /plugins/adapted` + `get_sl_data.adapted_plugins`；只读 `GET /plugins/<id>/<route>` |

从 EXILED 迁过来：DLL 放 `LabAPI/plugins/global/`；配置 `LabAPI/configs/<端口>/SLDataAPI/config.yml`；启停用 `properties.yml` 或 `/control/plugins`。

---

## 运行依赖

| 文件 | 版本 | 路径 |
|------|------|------|
| `0Harmony.dll` | 2.3.x | `%AppData%/SCP Secret Laboratory/LabAPI/dependencies/global/` |
| `Newtonsoft.Json.dll` | 13.0.x | 同上 |

缺失会加载失败。可从 [Harmony releases](https://github.com/pardeike/Harmony/releases)（net472）和 NuGet Newtonsoft.Json 13.0.x 获取，或从完整 LabAPI 安装目录复制。

编译需本机 SCP:SL 专用服务器与 LabAPI 目录，见 [Building](https://github.com/DNTOF/SLDataAPI/wiki/Building)。

---

## 安装

```bash
dotnet build -c Release
```

1. 确认上面两个依赖 DLL 已就位  
2. 复制 `SLDataAPI.dll` → `LabAPI/plugins/global/`  
3. 启动服务器，改生成的 `config.yml`，重启生效  
4. 防火墙放行 `http_port`（启用语音时再放 `voice_port`）

可选 MSBuild 覆盖：

```text
-p:SCPSL_DIR="...\SCPSL_Data\Managed"
-p:LABAPI_DIR="...\LabAPI"
```

---

## 配置（摘要）

路径：`LabAPI/configs/<端口或 global>/SLDataAPI/config.yml`（键名 **snake_case**）

```yaml
debug: false
verify_token: "your_secret_token"   # 只读 /get_sl_data
http_port: 8081
push_interval_seconds: 8

control_enabled: false              # 关则 /control/* 一律 404
control_token: "YourToken1!"        # 控制 + 语音；务必 ASCII 双引号包裹
control_transport: http             # http | ws（硬互斥）

voice_enabled: false
voice_port: 8082
voice_record_enabled: false
report_enabled: false
control_log_enabled: true
```

**常见坑：** 键名必须 snake_case；值格式错会导致整文件静默回退默认值；token 勿用弯引号，裸值勿以 `*` `&` `!` 开头。完整字段见 [Configuration](https://github.com/DNTOF/SLDataAPI/wiki/Configuration)。

---

## 鉴权（2.5）

| Token | 用途 | 推荐传入方式 |
|-------|------|----------------|
| `verify_token` | `/get_sl_data` | 请求头或 `?token=` |
| `control_token` | `/control/*` + 语音 WS | **`X-Control-Token` 头**（兼容查询串，但不推荐） |

`control_token` 启动时校验强度（长度与字符类）。数据口与控制/语音分表防爆破。详见 [Security-Model](https://github.com/DNTOF/SLDataAPI/wiki/Security-Model)。

---

## 接口入口

| 类型 | 地址 |
|------|------|
| 数据 | `GET http://<host>:8081/get_sl_data?token=<verify_token>` |
| 控制 HTTP | `POST http://<host>:8081/control/...` + `X-Control-Token` |
| 控制 WS | `ws://<host>:8081/control`（`control_transport: ws`） |
| 语音 | `ws://<host>:8082/ws` · `GET :8082/status` |

curl / 错误码 / WS 信封：[HTTP-API](https://github.com/DNTOF/SLDataAPI/wiki/HTTP-API) · [WS-Control-Protocol](https://github.com/DNTOF/SLDataAPI/wiki/WS-Control-Protocol) · [Voice-Forwarding](https://github.com/DNTOF/SLDataAPI/wiki/Voice-Forwarding)

`/control/command` 等价本机控制台权限——`control_token` 泄露即沦陷，默认关闭控制面是有意为之。

---

## AstrBot

[astrbot_plugin_sl_query](https://github.com/DNTOF/astrbot_plugin_sl_query) — `/bindlab <IP> <verify_token>` 后 `/sl` 显示 `[LAB]`。

---

## 开发

[Development-Guide](https://github.com/DNTOF/SLDataAPI/wiki/Development-Guide) · [Architecture](https://github.com/DNTOF/SLDataAPI/wiki/Architecture) · [Building](https://github.com/DNTOF/SLDataAPI/wiki/Building)

---

## 支持

- QQ 群：984840871  
- Issues：https://github.com/DNTOF/SLDataAPI/issues  

## 许可证

[GPLv3](LICENSE)
