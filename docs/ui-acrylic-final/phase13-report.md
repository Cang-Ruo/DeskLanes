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

**这四项仍不标通过**：真实锁屏 / 睡眠恢复、跨天 24h+、运行中真实硬件热插拔 / 改分辨率、
远程桌面会话。合成消息与 6 小时驻留只是部分证据。

## 六、回归与交付

| 项目 | 结果 |
|---|---|
| Release 构建 | **0 警告 0 错误** |
| 冒烟 | **460 / 460** |
| 三屏端到端 | **36 / 36** |
| 启动 | **7 / 7**，热中位 1.45 s，`errors.log` 未增长 |
| 名字牌像素 | p50 **6.90 / 最低 5.11**，静置 0 翻转 |
| 恢复事件 | 6 / 6 判据 true |
| 五张基线截图 | 最终构建重拍，5/5 活动可见、`BACKDROP=3`、侧栏 `CORNER=1` |
| 发布 | portable `91c6ac20…`（582,749 B）、selfcontained `ec53018c…`（65,099,504 B） |
| 安装同步 | `install.ps1 -Force` 覆盖安装，安装副本与发布副本 **MD5 一致**；开机自启与桌面图标隐藏未改动 |

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
