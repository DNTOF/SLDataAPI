# SLDataAPI 2.6 鉴权与端点契约（预览 = 成品）

> 状态：**Implemented in tree**（2026-09-05，API Key + 双轨数据口；预览合入前仍以本文件为准）  
> 目标：预览 `v2.6.0-preview*` → 11 月合入 `v2.6.0`  
> 原则：契约面向**所有第三方**（控制台 / 机器人 / 自建工具），**不绑定**某一前端产品命名。

---

## 0. 总览

| 通道 | 鉴权 | 配置 |
|---|---|---|
| `GET /get_sl_data` | **保持 2.5 原样**：`verify_token`（可用 query `?token=` 或既有头，行为与现网一致） | 主配置 `config.yml` 的 `verify_token` |
| 全部 `/control/*`、控制 WS、语音口 | **API Key**（生成后明文只显示一次） | 独立文件 **`apikey.config`** |

控制面：**无旧 `control_token` 兼容**。  
数据口：**特意保留 `verify_token`**，现有只读客户端（含 AstrBot 查询）不用改。

节点划分仍对齐原版 RA CATEGORIES（见 §4），便于按「值班只读信息 / 管理全开」授权。

---

## 1. 破坏性范围

| 保留 | 废弃（控制面） |
|---|---|
| `verify_token` + `/get_sl_data` 现有用法 | `control_token` 单密钥万能 |
| | 控制面 `?token=` / `?key=` / `X-Control-Token` |
| | 2.5 扁平控制路径（改为 §5 节点路径） |

---

## 2. `verify_token`（数据口，不变）

- 配置键仍在主配置（如 `config.yml`）：`verify_token`。  
- 仅保护 `GET /get_sl_data`。  
- 传参方式与 2.5.x **相同**（现网 AstrBot / 查询插件可继续用）。  
- 暴力破解锁定：仍走「读档」失败表，**不会**锁死 API Key 高权通道。

---

## 3. API Key（控制面 / 语音 / 事件）

### 3.1 文件：`apikey.config`

与主配置同级插件配置目录（LabAPI configs 下本插件目录），独立文件，避免和 `config.yml` 搅在一起。

建议结构：

```yaml
# apikey.config — API Key 与端点权限
# 密钥明文不会写回此文件；此处只存不可逆指纹 + 权限

# 端点权限目录：可按 path 或 path 前缀开关（细调）
# true = 允许；false = 拒绝；缺省 = 看模板/键默认
endpoint_catalog:
  "/control/player/data": true
  "/control/player/role": true
  "/control/player/effects": true
  "/control/player/inventory": false
  "/control/moderation/": true          # 前缀：其下 kick/ban/mute…
  "/control/admin/": true
  "/control/broadcast": true
  "/control/staffchat": true
  "/control/round/": true
  "/control/dummies/": true
  "/control/map/": true
  "/control/cassie": true
  "/control/console/": false
  "/control/plugins": false
  "/control/files/": false
  "/control/logs": true
  "/control/reports": true
  "/control/audit/list": true
  "voice:/ws": true                     # 语音端口路径用命名空间前缀区分
  "voice:/status": true
  "ws:subscribe_events": true

# 内置模板（生成 Key 时选用；生成后可再按 key 覆盖 endpoint 细表）
templates:
  duty:                                 # 值班模式：只能获取信息（含地图/定位类只读）
    description: "值班：只读信息（含地图定位只读），不可执行管理命令"
    endpoints:
      "/control/player/data": { read: true, write: false }
      "/control/map/": { read: true, write: false }   # layout/export/facility 查询、定位查询
      "/control/round/": { read: true, write: false } # 若有 status 类
      "/control/logs": true
      "/control/audit/list": true
      "ws:subscribe_events": true
      # 其余默认 false（含 moderation/admin/console/cassie 写、语音等按产品定）
      "voice:/ws": false
      "voice:/status": false
      "/control/moderation/": false
      "/control/admin/": false
      "/control/cassie": false
      "/control/console/": false
      "/control/plugins": false
      "/control/files/": false
      "/control/reports": false

  admin:                                # 管理模式：全套控制面权限
    description: "管理：控制面全开（不含 /get_sl_data，数据口仍用 verify_token）"
    endpoints: all_control_true         # 实现：展开为 catalog 内全部 true

keys:
  - id: "desk-duty-01"
    template: duty
    fingerprint: "sha256:…"             # 仅存哈希，不存明文
    created_at: "2026-09-05T01:00:00Z"
    note: "值班台"
    # 可选：覆盖模板，只开地图只读
    endpoints_override:
      "/control/map/": { read: true, write: false }
      "/control/player/data": { read: true, write: false }
      "/control/admin/": false          # 即使以后改模板也不给传送命令

  - id: "ops-admin-01"
    template: admin
    fingerprint: "sha256:…"
    created_at: "2026-09-05T01:05:00Z"
    note: "总控"
```

说明：

- **`endpoint_catalog`**：全站可授权端点清单，第三方文档以此为准。  
- **`templates.duty` / `templates.admin`**：默认两套；值班 = 信息获取（含地图相关**只读**与定位**查询**）；管理 = 控制面全开。  
- **`keys[].endpoints_override`**：对单把 Key 再砍细（「只看地图、不执行命令」）。  
- 匹配规则：最长前缀优先；**以 `/` 结尾的键为前缀**，不以 `/` 结尾的键为**精确匹配**；`read`/`write` 用于同一 path 上读写分离（如 map）。  
- 文件里**永不出现 API Key 明文**。

### 3.2 生成与「只显示一次」

管理入口（预览期二选一或都做，契约先定语义）：

1. **游戏内 / LocalAdmin 命令**（推荐）：如 `sldataapi apikey create <id> <duty|admin>`  
2. 可选：仅本机的一次性控制台输出  

流程：

1. 生成高熵随机 Key（建议 `sld_live_` / `sld_duty_` 前缀 + 随机段，便于识别环境）。  
2. **仅在创建命令响应里打印一次明文，并明确提示：关闭窗口即无法再查看。  
3. 落盘只写 `fingerprint`（如 SHA-256）+ `id` + 模板 + 覆盖表。  
4. 丢失只能 **revoke + 重新 create**，没有「找回明文」。  
5. `sldataapi apikey revoke <id>` / `list`（list 只显示 id、模板、创建时间、note，**无明文**）。

### 3.3 请求怎么带 Key

控制面 / 语音 / 控制 WS：

```
Authorization: Bearer <api_key_plaintext>
```

可选别名：`X-SLDataAPI-Key: <api_key_plaintext>`  

校验：对明文做与存储相同的指纹算法 → 命中 `keys[]` → 合并 `template` + `endpoints_override` → 检查当前 path 是否允许。  
失败：401（无效 Key）/ 403（Key 有效但端点未授权）。

控制 WS：握手必须带上述头；每个 `call.path` 单独按端点表检查；`subscribe_events` 查 `ws:subscribe_events`。

### 3.4 与 `verify_token` 的边界

| | `verify_token` | API Key |
|---|---|---|
| 作用域 | 仅 `/get_sl_data` | `/control/*`、控制 WS、语音口 |
| 配置文件 | 主配置 | `apikey.config` |
| 显示 | 配置里可见（与现网相同） | 明文只在创建时显示一次 |
| AstrBot `/sl` 查询 | **继续用这个，不用改** | 不需要 |

---

## 4. 节点与端点（对齐原版 RA，通用命名）

### 4.1 RA 面板对照

```
PLAYER:    Request Data | Role Management | Inventory | Status Effects
SERVER:    Moderation | Administration | Broadcasting | Staff Chat
GAME:      Round & Events | Dummies | Map Control | C.A.S.S.I.E.
```

### 4.2 路径（控制面）

**PLAYER**

| 路径 | 备注 |
|---|---|
| `/control/player/data` | Request Data / 档案读写 |
| `/control/player/role` | 强制角色 |
| `/control/player/inventory/*` | 物品；可 501 占位 |
| `/control/player/effects` | 状态效果 |

**SERVER · Moderation**

| 路径 |
|---|
| `/control/moderation/kick` |
| `/control/moderation/ban` |
| `/control/moderation/mute` |
| `/control/moderation/msg` |
| `/control/moderation/ban_list` |
| `/control/moderation/ban/add` |
| `/control/moderation/ban/revoke` |

**SERVER · Administration**

| 路径 | 备注 |
|---|---|
| `/control/admin/teleport` | 定位/传送（对齐 goto/bring，**不属于** Map Control） |
| `/control/admin/state` | 管理向状态 |

**SERVER · Broadcasting / Staff Chat**

| 路径 |
|---|
| `/control/broadcast` |
| `/control/staffchat` |

**GAME**

| 路径 | 备注 |
|---|---|
| `/control/round` | 回合 |
| `/control/round/warhead` | 核弹（Round & Events） |
| `/control/round/wave` | 波次 |
| `/control/dummies/*` | 占位 |
| `/control/map/facility` | 门/梯/灯 |
| `/control/map/layout` | 布局只读 |
| `/control/map/export` | 导出只读 |
| `/control/map/seed` | 种子；写权限单独可关 |
| `/control/cassie` | CASSIE（独立分类） |

**扩展（非 RA 页）**

| 路径 |
|---|
| `/control/console/command` |
| `/control/plugins`、`/control/plugins/slplayer` |
| `/control/files/*` |
| `/control/logs` |
| `/control/reports` |
| `/control/audit/list` |
| 语音口 `/ws`、`/status` |

### 4.3 值班模板 vs 管理模板（默认）

| | duty（值班） | admin（管理） |
|---|---|---|
| 意图 | 只获取信息 | 控制面全套 |
| 典型允许 | `player/data` 读、`map/*` 读、日志/审计读、事件订阅；**定位若只读坐标走 data/map 读，传送命令默认关** | moderation / admin（含 teleport）/ round / cassie / map 写 / … catalog 全开 |
| 典型拒绝 | moderation、admin 写、console、plugins、files、cassie 播报、voice 默认关（可在 override 打开） | — |
| `/get_sl_data` | 不走 API Key；用 `verify_token` | 同左 |

> 「值班能看地图定位」= **读**玩家/地图信息；**不是**默认给 `/control/admin/teleport`。若某部署希望值班也能传，在该 Key 的 `endpoints_override` 里打开即可。

### 4.4 旧控制路径 → 新路径

| 旧 | 新 |
|---|---|
| `/control/command` | `/control/console/command` |
| `/control/player/kick` 等 | `/control/moderation/...` |
| `/control/player/teleport` | `/control/admin/teleport` |
| `/control/map` | `/control/map/facility` |
| `/control/warhead` / `wave` | `/control/round/warhead` / `wave` |
| `/control/ban_*` | `/control/moderation/ban_*` |

---

## 5. 审计

- 控制面写操作写入 `control_log.json`。  
- `actor` = API Key 的 `id`（不是明文 Key）。  
- `POST /control/audit/list` 需在 `apikey.config` 里对该 Key 放开该 path。

---

## 6. 发版与自动更新

- 预览：tag 含 `preview|beta|rc` 且 GitHub Pre-release。  
- 生产自动更新不吃 preview（现有 `UpdateChecker`）。  
- `/get_sl_data` + `verify_token` 在预览/正式中行为连续。

---

## 7. 第三方接入要点（通用）

1. 只读监控：继续只配 `verify_token`，请求 `/get_sl_data`。  
2. 要远程控制：管理员在服上 `apikey create`，选 `duty` 或 `admin`，**立刻保存**返回的 Key。  
3. 控制请求带 `Authorization: Bearer <key>`；按 403 调 `endpoints_override` 细权。  
4. 文档与示例**不要**写死某一控制台品牌字段名。

---

## 8. 非目标

TLS 内置、Webhook 出站、录音远程 Timeline、15.0 房间字段、控制面旧 `control_token` 兼容。

---

## 9. 修订记录

| 日期 | 变更 |
|---|---|
| 2026-09-05 | 初稿；节点化；按 RA 面板重分 |
| 2026-09-05 | **API Key 模型**：`apikey.config`、明文只显示一次、duty/admin 模板、端点细调；去掉产品品牌化示例字段；**`/get_sl_data` 保留 `verify_token` 原样** |
| 2026-09-05 | 实现注记：非 `/` 结尾 ACL 键改为精确匹配；控制路径按 §4 重分且无旧别名；`control_token` 废弃 |
