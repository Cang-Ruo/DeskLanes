# 阶段 12：恢复事件处理路径（含消息级实测）与任务列表收口

> 一句话结论：把阶段 8 一直「只做代码审查、不新增未实测生产代码」的**电源 / 会话恢复**
> 补成了可运行的路径，并用合成系统消息拿到了可核验证据：
> `WM_POWERBROADCAST(resume)`、`WM_WTSSESSION_CHANGE(unlock)`、`WM_DISPLAYCHANGE`
> 三条都触发强制亮度重算（计数 0→1→2→3）、两次全量重扫完成日志（111 ms / 141 ms）、
> `errors.log` 零增长、进程无崩溃。
> **但这是消息级证据，不是真实锁屏 / 睡眠 / 跨天 24h+ / 硬件热插拔实测**，这些仍不标通过。

证据：`phase12-evidence.json`、`phase12-resume-events.json`、最终构建的五张基线截图与 `env.json`。

---

## 一、任务列表清理

分阶段开发留下的 17 条历史任务（4 条 in_progress、13 条 pending）逐条按仓库产物核实后收口，
**没有凭空把未做的标成通过**：

| 类别 | 任务 | 核实依据 |
|---|---|---|
| 阶段 4 验收 | #30 / #31 / #33 | `docs/final-delivery-report.md`：阶段 4 真实验收 run11/run12 均 48/48；冒烟现已 460/460 |
| 早期收尾 | #34 / #35 / #36 / #37 | v0.2.0 玻璃改版、`stage7-release-review.md`、`release-readiness-check.md`、最终交付报告与发布说明 |
| 长时驻留 | #56 / #65 | `docs/stage9-8-soak/report.md` + `soak.csv`（**72 点 / 6 小时**，不是 8 小时） |
| 收尾与文档 | #62 / #63 / #64 / #66 | `publish/vm-package`、提交 c7b656e 与 55e5a81、CHANGELOG / 路线图 / 已知限制、Round G/H 与 36/36 端到端 |
| 玻璃改版旧阶段 | #71 / #72 / #73 / #74 | `ui-acrylic-probe` / `ui-acrylic-v2` / `phase1~6` 报告与验收报告 |

现在任务列表 **95 项全部 completed，0 in_progress / 0 pending**。
其中 `#56` 的描述明确写明「实测 6 小时而非 8 小时，8 小时目标以 6 小时平台期结论替代，
跨天 24h+ 仍列为已知限制」，避免把口径收窄的事实抹掉。

---

## 二、为什么现在可以做恢复事件

阶段 8 的原话是「不新增未经实测的生产代码」——当时既没有订阅系统事件，也没有办法在
不打断会话的情况下验证。本阶段的取舍是：

- 不真锁屏、不真睡眠、不拔显示器（会打断当前会话，也拿不到用户凭据）；
- 改为把**合成的系统消息**直接投递给应用自己的隐藏消息宿主窗口
  （`DeskLanesMessageHost`，不广播给其它程序），验证处理路径确实跑通；
- 如实标注证据等级：消息级 ≠ 真实电源事件 / 24h+ 驻留。

这样既补上了代码，也没有把「未实测」伪装成「已通过」。

## 三、实现

`AppController`：

- `CreateMessageHost()` 里 `WTSRegisterSessionNotification(handle, NOTIFY_FOR_THIS_SESSION)`，
  `Dispose()` 里反注册；失败不阻断启动。
- `OnHostMessage` 新增分支：
  - `WM_POWERBROADCAST` + `PBT_APMRESUMESUSPEND` / `PBT_APMRESUMEAUTOMATIC`（睡眠恢复）
  - `WM_WTSSESSION_CHANGE` + `WTS_SESSION_UNLOCK` / `WTS_CONSOLE_CONNECT`（解锁 / 控制台连接）
  - `WM_DISPLAYCHANGE` 继续走钳位，并把亮度刷新改成 `force: true`
- `OnSystemResume(reason)` 统一做四件事：写一条可核验日志、
  `DynamicTextService.OnEnvironmentChanged(force: true)`、
  `ClampWindowsToScreen()`、`SyncNowAsync()` 全量重扫并把结果写进 `sync.log`。
  全程不重启应用、不改配置、不碰任何桌面文件。

`DynamicTextService`：

- `OnEnvironmentChanged(bool force)`：恢复 / 显示器变化时跳过「壁纸路径 + 网格数」签名去重
  （壁纸没变但 DWM 合成可能已变），诊断新增 `forced_refreshes` 计数。

## 四、实测（`tools/phase12-resume-events.py`）

| 合成消息 | 投递 | `forced_refreshes` | 结果 |
|---|---|---:|---|
| `WM_POWERBROADCAST(PBT_APMRESUMESUSPEND)` | ✓ | 0 → 1 | ✅ |
| `WM_WTSSESSION_CHANGE(WTS_SESSION_UNLOCK)` | ✓ | 1 → 2 | ✅ |
| `WM_DISPLAYCHANGE` | ✓ | 2 → 3 | ✅ |

- 亮度来源 `screen_capture` → `wallpaper_bitmap`，`desktop_refreshes` 0 → 3；
- `sync.log` 有两条「电源/会话恢复 … 触发全量重扫」和两条
  「恢复后全量重扫完成：新增 0 / 失效 0 / 重命名 0 / 跳过 False，耗时 111 / 141 ms」；
- `errors.log` 大小不变、进程仍存活；
- 全部 6 条判据为 true（含 `full_rescan_completed`）。

---

## 五、全量回归与交付

| 项目 | 结果 |
|---|---|
| Release 构建 | **0 警告 0 错误** |
| 冒烟 | **460 / 460** |
| 三屏端到端 | **36 / 36** |
| 启动 | **7 / 7**，热中位 1.35 s，`errors.log` 未增长 |
| 玻璃透光 | 空玻璃格 **0.616 / 不透明 −0.001 → PASS** |
| 运行中换壁纸（阶段 11 工具复跑） | 5 / 5 判据 true |
| 动态文字像素 | p50 **6.77 / 最低 5.11**，静置 0 翻转 |
| 五张基线截图 | 最终构建重拍，5/5 活动可见、每例清场 8 个窗口、`BACKDROP=3`、侧栏 `CORNER=1` |
| 发布 | portable `c06f229d…`（578,653 B）、selfcontained `ad624cd3…`（65,097,208 B） |
| 安装同步 | `install.ps1 -Edition selfcontained -Force`，安装副本与发布副本 **MD5 一致**；开机自启与桌面图标隐藏未改动 |

---

## 六、仍未解决（不标通过）

1. **真实锁屏 / 睡眠 / 休眠后的窗口与托盘行为**：只有合成消息证据；无人值守时脚本不敢锁屏，
   需要你手动配合一次。
2. **跨天 24h+ 驻留**：6 小时无泄漏结论已有，24h+ 未跑。
3. **运行中真实硬件热插拔 / 改分辨率**：只有 `WM_DISPLAYCHANGE` 消息级证据。
4. **远程桌面会话**：DWM 背景可能回退，未实测。
5. 固定底色的按钮 / 输入框 / 状态栏 / 面板标题栏文字仍不参与局部亮度自适应。
6. 布局切换耗时随项目数增长（阶段 8 结论），未动。

发布用清单 `docs/known-limitations.md` 与 `acceptance-report.md` 已同步到本阶段口径。

---

## 七、复现

```bash
# 恢复事件消息级验证（不会真的锁屏/睡眠）
python tools/phase12-resume-events.py

# 回归
dotnet run --project tests/DeskLanes.SmokeTests -c Release --no-build
python tools/verify-floating-interaction.py
python tools/measure-startup.py
python tools/phase11-wallpaper-refresh.py
python tools/phase10-dynamic-proof.py --wall=light
```
