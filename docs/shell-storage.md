# 壳存储模型（`settings.json` / `widgets.json`）

> 状态：契约草案，2026-10-31（Q14 补齐，已确认方向）
> 关联 [需求基线](requirements.md) Q14 · [组件契约](../../docs/contracts/widget-contract.md) · [宿主端点](host-endpoints.md)
> 配套 Schema：[settings.schema.json](../../DesktopAgent/contracts/schemas/settings.schema.json) · [widgets.schema.json](../../DesktopAgent/contracts/schemas/widgets.schema.json)

---

## 1. 边界

两份文件只存 **UI 状态与壳级设置**：

| 文件 | 只放 | 不放 |
| --- | --- | --- |
| `settings.json` | 壳级开关：自启、组件上限、热键、语言、日志 | 组件位置、config 值、权限、应用版本、hash |
| `widgets.json` | 按 `widgetId` 的组件状态：显隐、层级、位置、config、权限 | token、业务数据、应用数据库、工具清单 |

**绝不写**：`SDEMO_APP_TOKEN`、`client-token`、MCP 工具 schema、应用业务字段、日志正文。

## 2. 位置与权限

```
%APPDATA%\SDemo\DesktopAssembly\
├─ settings.json        ← 壳级设置
├─ widgets.json         ← 组件状态
├─ apps\                ← 安装目录（清单与 payload）
└─ widgets.json.bak     ← 上一次成功写入的备份（最多一份）

%LOCALAPPDATA%\SDemo\DesktopAssembly\
├─ runtime\host.json    ← 端口 / revision（不漫游）
└─ runtime\client-token ← 客户端 token（不漫游、不备份）
```

- `%APPDATA%` 文件权限继承用户目录 ACL；token 与端口一律在 `%LOCALAPPDATA%`。
- 文件编码 UTF-8（无 BOM），缩进 2 空格，camelCase；文本换行 LF。
- 单文件上限 256KB；组件上限默认 8（`settings.json.components.limit` 可调 1–8）。

## 3. `settings.json`

```json
{
  "schemaVersion": 1,
  "startup": {
    "launchAtLogin": true,
    "startHidden": true
  },
  "components": {
    "limit": 8
  },
  "hotkeys": {
    "toggleClickThrough": "Ctrl+Alt+X",
    "toggleWidgets": "Ctrl+Alt+D"
  },
  "language": "zh-CN",
  "logging": {
    "level": "info",
    "keepDays": 14
  }
}
```

| 字段 | 类型 | 默认 | 说明 |
| --- | --- | --- | --- |
| `schemaVersion` | int | `1` | 不兼容变更时 +1 |
| `startup.launchAtLogin` | bool | `true` | 壳自身开机自启（Q6）；**只有壳注册 Run 项**，应用由壳拉起 |
| `startup.startHidden` | bool | `true` | 启动后只在托盘，不弹面板 |
| `components.limit` | int | `8` | 组件数量上限（1–8，Q5） |
| `hotkeys.toggleClickThrough` | string\|null | `null` | 穿透切换热键；`null` = 不注册 |
| `hotkeys.toggleWidgets` | string\|null | `null` | 全部组件显示/隐藏；与 Win+D 无关（Q11） |
| `language` | string | `zh-CN` | v1 只允许 `zh-CN` |
| `logging.level` | enum | `info` | `error` / `warn` / `info` / `debug` |
| `logging.keepDays` | int | `14` | 日志保留天数（1–90） |

热键冲突处理：注册失败不静默，写日志并在设置面板提示；**不自动改键**。

## 4. `widgets.json`

```json
{
  "schemaVersion": 1,
  "widgets": {
    "sdemo.planner.widget": {
      "enabled": true,
      "visible": true,
      "layer": "floating",
      "alwaysOnTop": true,
      "clickThrough": false,
      "opacity": 0.92,
      "anchor": { "corner": "top-right", "offsetX": 24, "offsetY": 24 },
      "lastMonitorKey": "sid:9f2c1a7b|1920x1080|144",
      "positions": {
        "sid:9f2c1a7b|1920x1080|144": { "x": 1560, "y": 24, "width": 320, "height": 420 },
        "sid:5b1e0c33|2560x1440|120": { "x": 2180, "y": 24, "width": 320, "height": 420 }
      },
      "config": { "showCompleted": false, "maxItems": 8 },
      "startupOverride": null,
      "permissions": {
        "http-localhost": "granted",
        "notifications": "ask"
      }
    }
  }
}
```

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `enabled` | bool | 允许加载；`false` 时组件不加载、`ensure` 返回 `APP_DISABLED` |
| `visible` | bool | 当前是否显示；用户「隐藏」只改这个 |
| `startupOverride` | enum\|null | `onDemand` / `host` / `manual`；`null` 表示用 `app.json.startup` 的默认（Q16） |
| `layer` | enum | `floating` / `desktop`；v1 仅 native = `floating`，`desktop` 留给 wasm |
| `alwaysOnTop` | bool | 用户当前置顶状态；`layer=desktop` 时强制 `false` |
| `clickThrough` | bool | 当前穿透状态；与热键/菜单同步 |
| `opacity` | number | 0–1，保留两位；运行时再按清单 `opacity.min` 夹紧 |
| `anchor` | object | `corner` = `top-left/top-right/bottom-left/bottom-right`，`offsetX/offsetY` 为 DIP 整数 |
| `lastMonitorKey` | string\|null | 最近一次所在显示器；用于拔插后优先级判断 |
| `positions` | map | `monitorKey → rect`；rect 为**虚拟桌面绝对 DIP**，整数 |
| `config` | object | 只存值；类型按清单 `config` Schema 校验；单组件序列化 ≤4KB |
| `permissions` | map | `granted/denied/ask`；缺省由清单 `permissions` 生成 |

**默认值（C6）**：清单首次出现时宿主补一条默认状态——`enabled=true`、`visible=true`（装完即显示）、`layer` 取清单（v1 恒为 `floating`）、`alwaysOnTop` 取 `behavior.alwaysOnTopDefault`、`opacity` 取 `behavior.opacity.default`、`anchor` 取清单 `position`、`permissions` 里 `http-localhost` 默认 `granted`、其它默认 `ask`。

**未知字段保留**：根对象与每个 widget 条目都用 `[JsonExtensionData]` 保留未知键，旧壳读写新文件不丢配置（向前兼容）。

**`lastError` 不落盘**：错误进日志与诊断面板；落盘会让下次启动误报。

## 5. 位置、显示器与坐标

### 5.1 `monitorKey`

```
monitorKey = "sid:" + sha256(稳定显示器标识) 的前 8 个十六进制字符 + "|" + WxH + "|" + dpi
```

- **稳定显示器标识**优先用 EDID（制造商 + 产品码 + 序列号）；取不到时退化到 `DISPLAY_DEVICE.DeviceName`（`\\.\DISPLAY1`），并把前缀改为 `dev:` 以示退化。
- `WxH` 用**物理像素分辨率**（不是工作区），`dpi` 用该显示器有效 DPI（96 / 120 / 144 …）。
- 同一显示器改缩放 → 新 key；旧 key 保留在 `positions` 里，不删除（换回来还能用）。

### 5.2 坐标

- 存**虚拟桌面绝对坐标（DIP，整数）**，含负坐标（主屏左侧的副屏）。
- **混合 DPI 口径（C8）**：DIP 换算以组件所在显示器的有效 DPI 为基准；跨到不同缩放的显示器时按目标屏 DPI 重映射并写回新 key。混合 DPI 的跨多屏精确往返不在 v1 承诺（验收覆盖单屏 100%/125%/150% 与同缩放多屏）。
- `width/height` 存用户调整后的实际尺寸（DIP），加载时按清单 `size.minSize` / `maxWidth/maxHeight` 夹紧。
- 加载顺序：
  1. 当前 `monitorKey` 的 rect；
  2. 否则 `lastMonitorKey` 的 rect；
  3. 否则 `anchor` 角 + 偏移；
  4. 最后做 **80×80 可见性校验**（与当前任一显示器工作区相交区域的宽、高各 ≥ 80 DIP）；不通过回落到第 3 步。
- 拔掉显示器：该 key 的 rect 保留；迁移算法按 host-protocol §8（主屏右下角、按 widgetId 排序错开 24px），迁移结果写回新 key，**不覆盖旧 key**。
- 吸附与 `snapPx`：松手吸附后的 rect 直接落盘；吸附值不单独存。

## 6. 写入、备份与损坏恢复

### 6.1 原子写

```
1. 序列化到内存（先校验，再写盘）
2. 写 widgets.json.tmp（同目录，FileStream + Flush(true)）
3. 目标存在 → File.Replace(tmp, target, target.bak)
   目标不存在 → File.Move(tmp, target)
4. 失败保留 tmp，下次启动清理
```

- 防抖：位置/透明度/config 变化合并 500ms 写一次；退出前强制刷盘。
- 单写者：只有壳进程写；应用只能通过 `request` 改意图，不直接写文件。
- 并发：进程内 `SemaphoreSlim(1,1)` 串行化；跨进程靠壳互斥量。

### 6.2 损坏恢复

| 情况 | 处理 |
| --- | --- |
| JSON 语法错误 / 类型不符 | 重命名为 `widgets.corrupt-<yyyyMMddTHHmmssZ>.json`，按默认启动，日志记一条；**不阻塞启动** |
| `schemaVersion` 更高 | 只读模式：加载但**不回写**，托盘提示「设置需要更新 DesktopAssembly」 |
| `schemaVersion` 低 | 依次跑迁移函数，成功后原子写回新版本 |
| `.bak` 可用且主文件坏 | 优先尝试 `.bak`；仍失败才走默认 |
| 单个 widget 条目非法 | 跳过该条目并保留原文件内容，其它条目正常 |

- 损坏文件最多保留 3 份，超出按时间删除最旧。

## 7. 版本与迁移

| 版本 | 变更 | 迁移 |
| --- | --- | --- |
| v1 | 本文件描述的初始版本 | 文件缺失 → 内存默认，首次写入时建 v1 |
| v2+ | 只允许**加字段 / 加可选键** | 旧壳忽略未知字段；新壳读旧文件补默认 |
| 破坏性变更 | 改语义 / 改类型 → `schemaVersion + 1` | 必须提供 `MigrateVnToVm` 纯函数 + 单测 |

迁移函数签名建议：`WidgetsFile Migrate(WidgetsFile source, int from, int to)`；不读写磁盘，便于单测。

## 8. 校验与上限

| 项 | 规则 |
| --- | --- |
| 组件数 | ≤ `components.limit`（≤8）；超出的条目保留但标记 `blocked`，不加载 |
| 单组件 `config` | 序列化 ≤4KB；值只允许 boolean / number / string / null |
| `positions` | 每组件 ≤16 个 key；超出仅告警，v1 不淘汰 |
| `opacity` | 0–1；运行时夹紧到清单 `opacity.min` |
| `anchor.offset` | ±10000 DIP 内 |
| rect | 宽高 ≥1，≤ 显示器尺寸 4 倍；越界回落到锚点 |
| 文件 | ≤256KB；超限拒绝写入并记日志（不截断） |

## 9. 与清单的关系

```
widget.json（应用提供，只读）
   ├─ size / minSize / maxWidth / maxHeight  → 夹紧 widgets.json 的 rect
   ├─ opacity.default / min                  → 夹紧 opacity
   ├─ config Schema 子集                      → 校验 config 值类型
   ├─ permissions                            → 生成默认授权状态
   └─ layer / behavior                       → 提供默认值，用户覆盖存 widgets.json

app.json.startup（应用提供）                 → widgets.json.startupOverride（用户覆盖，null = 用清单）

widgets.json（宿主独占写）
   └─ 只覆盖「用户可改」的部分：显隐、层级、置顶、穿透、透明度、位置、config 值、权限、启动策略覆盖
```

**不要**把清单字段复制进 `widgets.json`；清单升级后应自然生效。

## 10. 测试

| 用例 | 断言 |
| --- | --- |
| Schema 快照 | `settings.json` / `widgets.json` 序列化结果与 `.schema.json` 校验通过 |
| 空文件 / 缺字段 | 补默认值，不抛异常 |
| 语法错误 | 生成 `.corrupt-*`，启动继续，布局回落锚点 |
| `.bak` 恢复 | 主文件坏、备份好 → 用备份 |
| 原子写 | 写入过程中断，目标文件仍是旧完整版本（无半截 JSON） |
| 迁移 v1→v2（假想） | 纯函数单测，不触碰磁盘 |
| 高版本只读 | 不覆盖文件，托盘提示 |
| monitorKey | 同显示器改缩放生成新 key；旧 key 保留 |
| 80×80 校验 | 屏幕外矩形回落锚点 |
| 未知字段 | 读→写后仍存在 |

## 11. 索引

| 主题 | 文档 |
| --- | --- |
| 组件清单字段 | [widget-contract.md](../../docs/contracts/widget-contract.md) |
| 宿主端点与 revision | [host-endpoints.md](host-endpoints.md) |
| 需求 Q14 与验收 | [desktop-assembly-requirements.md](requirements.md) |
</content>
