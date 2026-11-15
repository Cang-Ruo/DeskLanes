# 阶段 35：修「失焦自动收起」与阶段 34 收束形态的冲突

用户反馈「软件好像没有弄好」。先做无侵入诊断，再用真实配置复现并修复。

## 一、诊断

- `errors.log` 仍是 4810 字节、停在 2026-11-12 的旧 Owner 异常，**没有新异常**，
  应用没有崩溃。
- 安装副本 MD5 `217B1AD7…`（阶段 34 正式构建）正确；开始菜单项存在。
- 诊断时应用**没有在运行**。
- 用户真实配置：`WindowMode=0`（悬浮窗）、`CollapseOnLaunch=True`、
  **`FloatingHideOnDeactivate=True`**、**`SidebarAutoCollapse=True`**、
  `FloatingTopmost=False`。

用真实 exe 实测「启动 → 失焦 → Esc」的窗口状态：

```
1) 启动       悬浮窗 visible=True  (738,361 594x434)
2) 切到别的软件 悬浮窗 visible=False  ← 整个消失，没有小球
3) Esc        悬浮窗又可见，仍没有指示器窗口
```

## 二、根因：两条老路径绕过了收束状态机

| 设置 | 旧行为 | 阶段 34 规格要求 |
|---|---|---|
| `FloatingHideOnDeactivate=True` | `FloatingWindow.OnDeactivated` → `HideFloating()` **完全隐藏到托盘** | 收起 = 3cm 半透明小球 |
| `SidebarAutoCollapse=True` | `SidebarWindow.OnDeactivated` → `IsExpanded=false`，只折成 **44px 窄条**且窗口还在 | 收起 = 0.5cm 半透明边栏 |

也就是说：只要用户勾着这两个（很常见的偏好），一切到别的软件，
窗口就按老逻辑处理，**阶段 34 的小球 / 边栏根本不会出现**。
用户看到的「没弄好」正是这个。

### 为什么阶段 34 的 19/19 全过却没发现

`phase34-collapse-probe.py` 的 `patch()` 把
`FloatingHideOnDeactivate` / `SidebarAutoCollapse` **强制设成 False**，
只覆盖了「打开软件后自动收起」这一条入口，没覆盖
「失焦自动隐藏 / 自动收起」这两条老入口，也没用用户真实配置跑过。
**教训：验收探针不能把用户真实配置改掉后当作全绿。**

## 三、修法

- `CollapseVisualPolicy.ShouldUseCollapseIndicator(autoCollapse, suppress)`：
  勾了自动收起 → 必须统一走收束入口；设置窗口打开期间 `suppress=true` 优先不收。
- `FloatingWindow.OnDeactivated`：先试 `_controller.CollapseFromEscape()`
  （隐藏窗口 + 3cm 小球），只有用户**同时关掉了** `CollapseOnLaunch` 才沿用隐藏到托盘。
- `SidebarWindow.OnDeactivated`：同样先试收束入口（0.5cm 边栏），
  失败才回退旧的 `IsExpanded=false`；设置窗口打开期间保持展开。
- `_isCollapsed` 在 `CollapseToEdge()` 里先置位再 Hide，
  所以 Hide 触发的嵌套 `Deactivated` 不会递归，也不会误调 `HideFloating()`。
- 不改用户任何配置值，尊重他勾的偏好，只把「收起」这个动作统一。

## 四、新探针：`tools/phase35-deactivate-probe.py`

**只临时改 `WindowMode`，其余设置原样保留**（跑完写回原文）。
用真实配置 `FloatingHideOnDeactivate=True / SidebarAutoCollapse=True /
CollapseOnLaunch=True` 验证：启动 → 失焦 → 原窗口隐藏 + 指示器出现 →
悬停/点击展开，并检查指示器形态尺寸。

```
floating: 启动 / 失焦显示小球 / 小球 113×113 / 悬停不误展开 / 点击展开
sidebar : 启动 / 失焦显示边栏 / 边栏 46×1848 热区（可见玻璃 19px） / 悬停展开
                                                   合计 9/9 全过
```

探针开头必须 `_set_dpi_aware()`，否则 125% 缩放下
`GetWindowRect` 会把 113px 小球读成 90px（第一版踩过，已修）。

## 五、回归与交付

| 项目 | 结果 |
|---|---|
| Release 构建 | **0 警告 0 错误** |
| 冒烟 | **572 / 572**（新增第 42 节 3 条策略断言） |
| 启动探针 | **7 / 7**，`errors.log` 未增长 |
| 三屏端到端 | **连跑两次 37 / 37** |
| 阶段 31 收束探针 | **8 / 8** |
| 阶段 34 形态探针 | **19 / 19** |
| 阶段 35 真实配置失焦探针 | **9 / 9** |
| 三模式焦点一致性 | 0.01 / 0.0 / 0.03（舍入噪声，判定两态一致） |
| 发布 | selfcontained `6F7EFDA094595E4FBDD9CC2E85FFB495`、portable `E70376BFEC0F3DA709E2359D0B6BC66D` |
| 安装 | 覆盖安装，安装副本 MD5 与正式构建一致 |

为替换 exe 结束过运行中的实例，请重新按 `Win` → `DeskLanes` 启动。
现在切到别的软件时，悬浮窗会变成 3cm 半透明小球、侧边栏会变成 0.5cm
半透明边栏，点小球 / 悬停边栏就能叫回来。

## 六、仍需你确认

我复现并修的是「失焦后窗口整个消失、看不到小球」这一处。
如果你说的「没弄好」指的是别的现象（比如某个界面尺寸、透明度、
收起后窄条触发范围、或者根本打不开），请直接说具体是哪一步、看到什么，
我按现象再定位——不会盲改。
