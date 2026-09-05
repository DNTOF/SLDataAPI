# SLDataAPI 2.6 Auth — 测试结果

日期：2026-09-05（机器本地，UTC+8）

## 1. 单元测试 `tests/SLDataAPI.Auth.Tests`

命令：

```
dotnet test tests/SLDataAPI.Auth.Tests --logger "console;verbosity=normal"
```

结果：**通过 16 / 16**（总时长约 0.48s）

覆盖：

- SHA-256 指纹确定性 / 前缀 `sha256:`
- `EndpointGrant` bool 与 read/write 解析
- 最长前缀 ACL、精确 path、前缀 `/`、未命中拒绝
- duty / admin 模板合并与 `endpoints_override`
- 写操作判定（map layout vs facility、admin/state body、reports list）

链接纯逻辑源：`Auth/EndpointAcl.cs`（无 Unity / LabAPI 依赖）。

## 2. 主插件构建

命令：

```
dotnet build -c Release -p:SCPSL_DIR="D:\SteamLibrary\steamapps\common\SCP Secret Laboratory Dedicated Server\SCPSL_Data\Managed" -p:LABAPI_DIR="C:\Users\AllowCache\AppData\Roaming\SCP Secret Laboratory\LabAPI"
```

结果：**成功** — 0 警告 / 0 错误  
输出：`bin/Release/net48/SLDataAPI.dll`（Version 2.6.0）

## 3. 手工 / 游戏内检查清单

| 项 | 状态 |
|----|------|
| `GET /get_sl_data?token=<verify_token>` 仍可用（AstrBot） | **NOT RUN**（未启游戏服） |
| `sldataapi apikey create desk1 duty` 明文只显示一次并写入指纹 | **NOT RUN** |
| `sldataapi apikey list` 无明文 | **NOT RUN** |
| `sldataapi apikey revoke desk1` | **NOT RUN** |
| 控制 HTTP：Bearer 有效 key → 200；无效 → 401；duty 调 teleport → 403 | **NOT RUN** |
| 旧路径 `/control/player/kick` 等 → 404 | **NOT RUN** |
| 新路径 `/control/moderation/kick` + admin key → 成功 | **NOT RUN** |
| 控制 WS 握手仅头鉴权；`subscribe_events` 查 `ws:subscribe_events` | **NOT RUN** |
| 语音 `/ws` 需 `voice:/ws` 授权 | **NOT RUN** |
| `control_token` 配置时启动警告且不能鉴权控制面 | **NOT RUN** |
| 审计日志 `actor` = key id | **NOT RUN** |
| 缺省生成 `apikey.config` | **NOT RUN** |

## 4. 501 占位端点

- `/control/player/inventory`（及未实现的 inventory 子路径 → 当前仅精确 `/control/player/inventory`）
- `/control/broadcast`
- `/control/staffchat`
- `/control/dummies`

## 5. 风险备忘

- 第三方/WebUI 仍用旧路径或 `X-Control-Token` 会直接失败（刻意破坏性）。
- `apikey.config` 经 YamlDotNet 回写时模板格式可能与手写 YAML 略有差异（语义保留）。
- 命令依赖 LabAPI 自动发现 `CommandHandler`；需在真实服上确认 `sldataapi` 已注册。
- 语音口不再要求 `control_token`，但要求已创建且授权 `voice:*` 的 API Key。