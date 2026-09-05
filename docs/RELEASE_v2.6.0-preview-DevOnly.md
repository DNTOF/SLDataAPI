# v2.6.0-preview-DevOnly Kerckhoffs

> **Preview / DevOnly** 警告：本包仅供开发与联调，**勿**用于生产自动更新；正式 **2.6.0** 计划于 **11 月**合入 `main`。本 Pre-release 的 tag 含 `preview`，现有 UpdateChecker 不会自动安装。

> *"A cryptographical system should remain secure even when the adversary knows all the details of the system, except the key."*
> — Auguste Kerckhoffs, *La cryptographie militaire* (1883)
> （系统可以公开，真正要守住的只有密钥——以及谁被允许用它做什么。）

## 新增

### 1. API Key 鉴权与 apikey.config（双轨）

- **`GET /get_sl_data`** 仍走主配置 **`verify_token`**（与 2.5.x 行为连续，只读监控客户端不必改）
- **控制面 / 控制 WS / 语音口** 改走 **API Key**：`Authorization: Bearer <key>` 或 `X-SLDataAPI-Key: <key>`
- 独立文件 **`apikey.config`**（与 config.yml 同级）：只存指纹，**明文仅在创建时显示一次**
- 内置模板：**duty**（值班只读信息）/ **admin**（控制面全开）；可用 `endpoints_override` 按 path 细调
- 游戏内命令：`sldataapi apikey create|revoke|list`

### 2. 控制路径按原版 RA 面板重对齐

| 区域 | 代表路径 |
|------|----------|
| PLAYER | `/control/player/data` · `role` · `effects` · `inventory`（占位） |
| Moderation | `/control/moderation/kick|ban|mute|msg|ban_list|ban/add|ban/revoke` |
| Administration | `/control/admin/teleport` · `state` |
| Round & Events | `/control/round` · `round/warhead` · `round/wave` |
| Map | `/control/map/facility|layout|export|seed` |
| 扩展 | `/control/console/command` · `audit/list` · `logs` · `plugins` · `files/*` · `reports` |

旧扁平路径（如 `/control/player/kick`、`/control/command`）**无别名**，多数返回 ACL **403**（或 404）。

### 3. 审计 actor = key id

控制写操作写入 `control_log.json` 时，`actor` 为 API Key 的 **`id`**（不是明文 Key）。可通过 `/control/audit/list` 查询（需 ACL 放开）。

## 破坏性变更

- **`control_token` / `X-Control-Token` / 控制面 `?token=` / `?key=`** 不再作为有效鉴权（期望 **401**）
- **旧扁平控制路径**失效（现多返回 **403** ACL）
- 第三方控制台 / 机器人必须：换 Bearer Key + 改新 path

## 已知占位（501）

- `/control/player/inventory`
- `/control/broadcast`（也可能因 ACL 先返回 403）
- `/control/staffchat`
- `/control/dummies`

## 安装 / 更新

1. 将 `SLDataAPI.dll` 放入 `LabAPI/plugins/global/`（或你的全局插件目录）
2. 确认依赖：`LabAPI/dependencies/global/` 下有 `0Harmony.dll`、`Newtonsoft.Json.dll`
3. 重启服务器；首次会生成 / 补齐配置目录下的 **`apikey.config`**
4. 在 LocalAdmin / 游戏控制台：`sldataapi apikey create <id> admin`（或 `duty`），**立刻保存**返回的明文 Key
5. 数据口继续使用 `config.yml` 的 `verify_token`

本包为 **DevOnly 预览二进制**：请手动替换 DLL；不要依赖自动更新吃到本 tag。

## 升级须知（第三方）

1. 只读 `/get_sl_data`：一般**不用改**（仍 `verify_token`）
2. 任何 `/control/*`、控制 WS、语音口：改为 API Key 头；删除对 `X-Control-Token` 的依赖
3. 按上表迁移 path（尤其 kick/ban → `moderation/*`，teleport → `admin/teleport`，command → `console/command`）
4. 用值班 Key 时不要期望能踢人 / 传送 / 跑控制台——那是设计如此
5. 丢失的 Key 无法找回：只能 `revoke` 后重新 `create`

## DevKit

附件 `SLDataAPI-DevKit-v2.6.0-preview-DevOnly.zip` 内含 skill 文档与：

```powershell
.\scripts\Test-ControlEndpoints.ps1 -BaseUrl http://127.0.0.1:8081 `
  -VerifyToken "<verify_token>" -ApiKey "<admin_key>"
```

用于对**自有服务器**做鉴权与路径冒烟（禁止对未授权目标使用；勿把真实密钥写进脚本）。

## 完整变更

说明：源码将于 **11 月**随正式 **2.6.0** 合并进 `main`；本 Pre-release 提供 **DevOnly** 二进制与 DevKit 供联调。契约细节见仓库 `docs/AUTH_AND_API_CONTRACT_2.6.md`（预览树内）。