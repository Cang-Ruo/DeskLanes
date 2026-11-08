# 阶段 13：全文字块可读性结案、性能复核与人工配合项交付

> 一句话结论：把「固定底色控件文字不参与自适应」这条限制**真正审了一遍**，
> 结果它不是无害的范围说明，而是藏了真缺陷：状态栏 / 分类计数 / 搜索提示 /
> 关闭按钮在亮壁纸下分别只有 1.87 / 1.77 / 3.01 / 4.29。做了最小修复后，
> 亮 / 暗壁纸下当前可见的 **22 块文字全部 ≥ 4.5**。布局切换耗时复测确认
> 动态文字没有系统性性能回退（阶段 8「不改代码」结论成立）。
> 四条需要人配合的验收交付了只读的前后采集工具，仍不标通过。

证据：`phase13-evidence.json`、`phase13-all-text-{light,dark}{,-scroll}.json`、
`phase13-probe-demo-*.json`、五张重拍基线截图与 `env.json`。

---

## 一、发现：旧结论是错的

此前多个阶段都写着「固定底色的按钮 / 输入框 / 状态栏 / 面板标题栏文字本来不受亮壁纸影响」。
为验证这句话，给诊断模式加了 `all-text-blocks.json`：导出**所有**可见文字块的屏幕矩形、
真实前景色、是否接入自适应；再用截图像素逐块算 WCAG（前景用视觉树真实色，
背景用该块像素中位数——不用极值取样，否则整行宽的 TextBlock 会算出 1.0 的假数字）。

**修复前**（可视区 22 块）：

| 壁纸 | 低于 4.5 的块 | 典型值 |
|---|---|---|
| 亮 | 3 块 | 搜索提示 3.01、分类计数 `0` 1.77、状态栏「就绪」1.87 |
| 暗 | 2 块 | 分类计数 2.61、状态栏 4.12 |

根因有两类：
1. `BrushTextTertiary`（#8CA0B8）被用在**直接压在玻璃上**的小字（状态栏、计数）；
2. 搜索框只有 30% 深底，亮壁纸下托不住提示文字，关闭按钮甚至是透明底。

## 二、最小修复

| 改动 | 文件 |
|---|---|
| `BrushInputGlass` 30% → **45% 深底**（规格允许控件更明显） | `Themes/GlassTheme.xaml` |
| 搜索提示、关闭按钮改 `BrushTextPrimary` | `FloatingWindow.xaml` / `SidebarWindow.xaml` |
| 分类计数、底部状态栏包进 `dt:DynamicText.Scrim="True"` 的局部底（底与字成对切换，主窗口早就有 `StatusBarScrim`，这里是补齐一致性） | `FloatingWindow` / `SidebarWindow` / `CategoryNavView` |
| `PanelView` 标题栏本身是固定深底 `#2E17283B`，保持静态主文字色，**不接入动态色**（否则亮壁纸会误判成深字压深底） | `PanelView.xaml` |

> 关键认识：**中间灰玻璃是死区**——背景亮度约 110~140 时，浅字与深字都到不了 4.5。
> 所以只把字变深不够，必须同时给它一块会切换的局部底；这也是名字牌一直有 scrim 的原因。

**修复后**（可视区逐块像素实测）：

| 壁纸 | 测到块数 | 自适应块 p50 / 最低 | 固定底色块 p50 / 最低 | 低于 4.5 |
|---|---:|---:|---:|---:|
| 亮 | 22 | 7.25 / **5.21** | 12.76 / **9.04** | **0** |
| 暗 | 22 | 11.88 / **6.82** | 14.53 / **12.23** | **0** |

滚动场景：导出矩形会与被顶部栏裁掉的布局位置错位，逐块像素值**不可采信**
（结果 JSON 里标了 `pixel_audit_valid=false` 与原因）。改用应用的决策记录覆盖：
亮 / 暗各 28 个控件，最低 11.69 / 8.9，全部 ≥ 4.5。没有拿错位坐标编造通过。

## 三、玻璃透光的代价（如实记录）

控件底色加深、局部底变多，空白玻璃格减少了：

| 场景 | 空玻璃格相关（玻璃 / 不透明） |
|---|---|
| 零 UI 参照窗 | **0.901 / −0.092 PASS** |
| 带 UI 悬浮窗，阶段 12 | 0.616 |
| 带 UI 悬浮窗，本阶段三次 | **0.450（单次未过）/ 0.505 / 0.505 PASS** |

材质本身没变（参考窗仍是 0.901），带 UI 的窗口落到约 0.505，
仍高于判据 ≥0.5 且远高于不透明对照 −0.123，但**余量很薄**，
首次 0.450 也确实没过。这是「可读性优先于透明度」换来的实际代价，
记为已知风险，不隐藏、不调判据。

## 四、布局切换耗时复核

用最终构建重跑 UiSnapshot `toggle`（分类 ↔ 自由分区），并用
`DESKLANES_NO_DYNAMIC_TEXT=1` 做 A/B：

| 项目数 | 最终构建 | 关闭动态文字 | 阶段 8 基线 |
|---:|---:|---:|---:|
| 15 | 41.5 ms | 53.5 ms | 42 ms |
| 65 | 127.3 ms | 117.2 ms | 107 ms |
| 220 | 336.4 ms | 332.9 ms | ~300 ms |
| 515 | 499.6 ms | 484.4 ms | ~440 ms |

全部 **PASS(<1s)**；开 / 关动态文字的差值在噪声内（最大 +15 ms），
没有系统性回退。阶段 8「不为它动视图缓存或虚拟化」的结论继续成立。

## 五、四条人工配合项的交付

`tools/phase13-manual-probes.py`：`plan` / `capture` / `compare` / `rdp` 四个**只读**子命令。
它不会锁屏、不会睡眠、不会拔显示器、不会改分辨率，只在你手动操作前后各采一份状态
（窗口矩形、DWM 20/38/33、显示器拓扑、远程会话标志、日志大小、动态文字计数器），
再比对并列出问题。自测：应用运行时采到 3 个窗口，GDI+ 内部窗口与消息宿主已排除误报，
`compare` 结果 `issues=[]`。

跨天 24h+ 用现成的 `tools/soak-8h.py --hours 24`（脚本会拷私有副本跑，不占安装版）。
具体四条命令见 `python tools/phase13-manual-probes.py plan`。

### 第一次实跑暴露的三个工具缺陷（已修）

用户按说明在 **PowerShell** 里跑时踩到：

1. 说明里用了 Bash 的反斜杠 `\` 续行，PowerShell 不认，报
   `unrecognized arguments: \` 与「一元运算符 -- 后面缺少表达式」。
   → 现在 `plan` 里**全部改成单行命令**；`compare` 不带参数时自动读
   `phase13-probe-before.json` / `phase13-probe-after.json`，不用再敲长参数。
2. 窗口过滤按标题含 `DeskLanes` 匹配，把**「DeskLanes - 文件资源管理器」**
   当成应用窗口 —— 软件其实没运行，却采到 `windows: 1`，看起来像成功。
   → 改为按 `GetWindowThreadProcessId` + `QueryFullProcessImageNameW` 精确识别
   `DeskLanes.exe`；非业务窗口一律标 `helper`；快照新增 `app_running` 字段，
   为 false 时直接打印「这份快照无效」警告，`compare` 也会把结果判为无效。
3. 两次采集时间戳都是 `01:21:45`（同一秒），说明中间那步锁屏没真的执行。
   → 时间戳改带毫秒；`compare` 发现前后时间戳相同会明确提示
   「中间那步操作很可能没有真正执行」。

修完做了端到端自测：应用运行时 `app_running=true`、采到 10 个同进程窗口
（4 个业务窗口 + 辅助窗口）、投递合成恢复消息后
`forced_refreshes_delta=1`、`resume_event_observed=true`、`issues=[]`，
见 `phase13-probe-selftest-compare.json`。首轮那份无效采集已删除，不算证据。

> 给用户的正确姿势（PowerShell，全部单行）：
> `capture --label=before` → **真的按 Win+L 锁屏并解锁、等 3 秒** →
> `capture --label=after` → `compare`。前提是软件正在运行。

### 第 1 项已由用户实测通过（2026-11-09 01:38 → 01:40）

用户手动 `Win+L` 锁屏解锁，前后各采一次：

| 判据 | 结果 |
|---|---|
| `app_running` | true → true |
| `sync.log` 恢复事件 | **2 条** `WM_WTSSESSION_CHANGE/unlock`（01:39:55.278/.280） |
| 全量重扫 | **2 次完成**（一次因「已有同步在进行」正常跳过，一次 310 ms） |
| `errors.log` | 3634 → 3634 零增长 |
| 窗口 / 拓扑 | 11 → 11，三屏 `(-1080,0) 6200×1920` 不变 |
| `issues` | `[]` |

证据：`phase13-probe-before.json` / `phase13-probe-after.json` / `phase13-probe-compare.json`。

> **又一次判据修正**：首版 `compare` 用 `dynamic-text.json` 的 `forced_refreshes_delta`
> 判断「有没有恢复」，但那份 JSON 只在 `DESKLANES_DIAG_TEXT=1` 启动时写；
> 用户从开始菜单正常启动，它停在 01:26 自测的旧值，于是误报
> `resume_event_observed=false`。现在改为**以 `sync.log` 里两次采集之间的恢复日志为主判据**
> （`OnSystemResume` 走常开的 FileDiagnosticLogger，与诊断开关无关），
> 诊断计数降级为辅助并在结果里注明「正常启动恒为 0，不作主判据」。
> 真实用户数据重跑后 `resume_event_observed=true`。

### 运行中改分辨率：三块屏各测一次，均 PASS（自动还原）

> **屏幕身份订正（用户 2026-11-09 指出）**：此前把 `\\.\DISPLAY1` 叫「右侧副屏」是错的。
> 真实拓扑是：`DISPLAY2 (-1080,0) 1080×1920` = **左侧竖屏（外接）**；
> `DISPLAY15 (0,0) 2560×1440` = **主屏（外接，NVIDIA RTX 5060）**；
> `DISPLAY1 (2560,0) 2560×1600` = **右侧笔记本内置屏**（软件当时显示在这块）。
> 用户要求后续测试走左侧与主屏，避开笔记本屏。

新增 `tools/phase13-display-probe.py`：

- 默认 `--dry` 只打印计划；`--apply` 才真的改；`try/finally` 无条件还原；
  `--restore-only` 兜底；原始 DEVMODE 以 hex 写进证据 JSON
- `--target=left|primary|laptop`，**默认 `left`（左侧竖屏）**，碰笔记本屏必须显式指定；
  证据文件名按目标屏区分，互不覆盖
- 以当前完整 DEVMODE 为基底只改宽/高/刷新率，保留位置
- 竖屏模式宽度本来就小，模式枚举阈值从 `w>=1280` 放宽到 `w>=640`

**第一份（历史，笔记本屏上）**：复用用户正在运行的实例
（`started_app_here=false`，是真正的「运行中」）：

| 项 | 结果 |
|---|---|
| 改动 | `2560×1600@180Hz → 1920×1200@60Hz`，`rc=0` |
| 虚拟屏 | `(-1080,0) 6200×1920 → (-1080,0) 5560×1920` |
| 应用 | `app_running=true` 前后，窗口 9 → 9 |
| `errors.log` | 3634 → 3634 零增长 |
| 还原 | `rc=0`，回到 `2560×1600@180Hz @ (2560,0)`，`restored_ok=true` |
| 判据 | `virtual_screen_changed=true`、`errors_log_unchanged=true`、`unexpected_issues=[]` → **PASS** |

> 判据口径修正：`compare` 原本把「显示器拓扑变了」记进 `issues`，
> 但那正是本测试要制造的事件 → 改放 `notices`，`issues` 只留真问题。
> 另外改分辨率走 `WM_DISPLAYCHANGE` 分支（钳位 + 亮度重算），**不写**
> 「电源/会话恢复」日志，所以 `resume_log_count=0` 属正常，不能拿它判失败。

证据：`phase13-display-probe.json`、三份 `phase13-probe-disp-*.json`、
`phase13-probe-display-compare.json`。该份保留为历史结果，不删除。

**用户澄清后又补测两次**（02:0x，同样复用运行中的实例）：

| 目标屏 | 改动 | 虚拟屏变化 | 还原 | 结论 |
|---|---|---|---|---|
| 左侧竖屏 `DISPLAY2` | `1080×1920 → 1050×1680` | `(-1080,0) 6200×1920 → (-1050,0) 6170×1680` | `rc=0`，回到 `1080×1920 @ (-1080,0)` | **PASS** |
| 主屏 `DISPLAY15` | `2560×1440 → 1920×1200` | `6200×1920 → 5560×1920` | `rc=0`，回到 `2560×1440 @ (0,0)` | **PASS** |

两次都是 `errors.log` 零增长、窗口 9/9、`issues=[]`。跑完只读复核三屏：
`DISPLAY1 (2560,0) 2560×1600`、`DISPLAY2 (-1080,0) 1080×1920`、
`DISPLAY15 (0,0) 2560×1440` —— **全部与原状一致**。
证据：`phase13-display-{left,primary}-probe.json` 及各自的
`phase13-probe-display-{left,primary}-compare.json`。

### 物理热插拔：用户实测 **FAIL**，查出真缺陷（已修复，待复测）

用户 02:24–02:26 跑了 `--unplug`，拔下左侧竖屏并按两次回车，三份快照齐全。
**首版工具报 PASS 是误判**：`compare` 跳过了 `minimized` 窗口的几何检查。
严格判据重算后改判 **FAIL**：

| 阶段 | 显示器 | 虚拟屏 | 悬浮窗 | 判定 |
|---|---|---|---|---|
| 拔前 | 3 台 | `(-1080,0) 6200×1920` | `[-1036,240,-41,1403]` 995×1163 可见 | 正常 |
| 拔下 | **1 台** | `(0,0) 2560×1440` | `[-1043,200,-214,1169]` **完全在屏幕外**、被最小化 | ❌ |
| 插回 | 3 台（用户手动改回竖屏，Windows 重排） | `(-1080,-154) 6200×2439` | `[-172,1639,168,1687]` **340×48 细横条**、仍最小化 | ❌ |

截图里红框那个灰色小条就是被压扁的悬浮窗。`errors.log` 全程 3634 不变。

**根因**（两处产品缺陷 + 一处取证缺陷）：

1. `AppController.ClampWindowsToScreen()` 读 WPF 缓存的 `SystemParameters.VirtualScreen*`，
   `WM_DISPLAYCHANGE` 到达时常常还是旧的三屏矩形，于是 `-1043` 被判为"在范围内"不移动；
   且 Windows 会连发数次该消息、用户随后还会手动改回竖屏，单次钳位会踩空。
2. `MonitorService.FitIntoWorkArea()` 在热插拔中间态拿到极小的"工作区"时仍照常收缩，
   把窗口压成 340×48 细条。
3. `phase13-manual-probes.py` 的 `compare` 跳过 `minimized` 窗口，也不检查尺寸退化，
   于是把明显异常的状态报成 `issues=[]` / PASS。

**修复**：

- `WM_DISPLAYCHANGE` 立即钳位 + 300 ms / 1200 ms 两次延迟重钳；拓扑事件下
  `restoreMinimized=true` 恢复被系统最小化的窗口（日常呼出不打断用户主动最小化）
- `ClampWindow` 增加最小尺寸保护（非有限 / 小于 `MinWidth/MinHeight` 时恢复）与非有限位置兜底
- `FitIntoWorkArea` 遇到异常小工作区直接跳过收缩
- 钳位计算抽成 `MonitorService.ClampToVirtualScreen()`、异常判定抽成 `IsWorkAreaSane()`，
  冒烟新增 6 条可重复回归断言（**466 / 466**）
- `compare` 不再跳过 minimized 窗口，新增「完全在虚拟屏外」与「尺寸退化成细条」两类 issue

**取证工具的二次修正**：首版 `compare` 用 `dynamic-text.json` 判恢复（只在诊断模式写）、
第二版跳过 minimized 窗口，两次都容易误报。现在几何检查覆盖所有可见业务窗口。

**当前状态**：用户那个细条窗口已用 Win32 `ShowWindow(SW_RESTORE)` + `MoveWindow`
恢复成 `1250×875` 位于主屏可见区（不写配置、不动 soak 进程）。
但**修复要等重启软件才生效**——当前运行的仍是 01:50 启动的旧实例（同时是 24h soak 进程），
不能杀。真实拔插复测需用户下次方便时再拔一次，工具判据已修，不会再误报 PASS。

**仍未实测**：真实睡眠 / 休眠唤醒（合成 `WM_POWERBROADCAST` 只算消息级证据）、
跨天 24h+ 驻留（正在跑）、热插拔**修复后复测**。远程桌面会话用户明确暂缓。
已完成并从「未实测」移出的：锁屏 / 解锁、运行中改分辨率（三次）。

## 六、回归与交付

| 项目 | 结果 |
|---|---|
| Release 构建 | **0 警告 0 错误** |
| 冒烟 | **466 / 466**（新增 6 条热插拔钳位回归） |
| 三屏端到端 | **36 / 36** |
| 启动 | **7 / 7**，热中位 1.45 s，`errors.log` 未增长 |
| 名字牌像素 | p50 **6.90 / 最低 5.11**，静置 0 翻转 |
| 恢复事件 | 6 / 6 判据 true |
| 五张基线截图 | 最终构建重拍，5/5 活动可见、`BACKDROP=3`、侧栏 `CORNER=1` |
| 发布（热插拔修复后） | portable `f1aaceb1…`（586,845 B）；selfcontained 新版 `2a5b17d9…`（65,100,216 B）**在 `publish/win-x64-selfcontained-next/`** |
| 安装同步 | **本轮暂未覆盖安装**：`publish/win-x64-selfcontained/DeskLanes.exe` 正被 24h soak 进程（PID 286804，01:50 启动）占用，`GenerateBundle` 报 `UnauthorizedAccessException`；`install.ps1` 也会结束该进程。等 soak 跑完再把 `next` 覆盖到正式目录并 `install.ps1 -Force`。开机自启与桌面图标隐藏全程未改动 |
| 未跑的回归 | 三屏端到端 / 启动探针会启动新实例，与单实例互斥及 soak 冲突，本轮未跑；冒烟 466、改分辨率 left 回归 PASS、`errors.log` 零增长已覆盖 |

新增 `phase13-report.md`、`phase13-evidence.json`、`tools/phase13-all-text-audit.py`、
`tools/phase13-manual-probes.py`；同步 `phase10/11/12-report.md` 与 `acceptance-report.md`
里已过时的限制表述。

---

## 七、复现

```bash
# 全文字块像素审计（静态可信 / 滚动态看决策记录）
python tools/phase13-all-text-audit.py --wall=light
python tools/phase13-all-text-audit.py --wall=dark

# 布局切换 A/B
DESKLANES_MEM_ITEMS=205 tests/DeskLanes.UiSnapshot/bin/Release/net10.0-windows/DeskLanes.UiSnapshot.exe toggle out.json --demo
DESKLANES_NO_DYNAMIC_TEXT=1 DESKLANES_MEM_ITEMS=205 ...同上...

# 人工配合项
python tools/phase13-manual-probes.py plan
python tools/phase13-manual-probes.py capture --label=before
# ...你做那件事...
python tools/phase13-manual-probes.py capture --label=after
python tools/phase13-manual-probes.py compare --before=... --after=...
```
