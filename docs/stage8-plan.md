# 阶段 8：发布前可靠性与数据安全验证（执行计划）

> 建立时间：2026-10-26（UI 轻玻璃改版收官后，用户决策：冻结 UI，转入发布前验证）
> 依据：`docs/project-overview-and-roadmap.md`、`docs/release-readiness-check.md`、
> `docs/ui-redesign/report.md`
> 本文件是阶段 8 的**唯一执行口径**；每周巡检（`docs/release-readiness-check.md`）按本文件的四步核对进度。

## 0. 已冻结 / 明确不做（本阶段不再讨论）

- **UI 视觉设计冻结**：轻玻璃改版已完成（`docs/ui-redesign/report.md`）。不再新增动画、主题系统、
  复杂卡片特效、新的窗口模式或自动更新。
- **不增加功能**：阶段 8 只做验证、加固与发布准备。
- **悬浮窗尺寸持久化**（`WorkspaceModels.cs` 的 `FloatingWindowWidth/Height` 仍是死字段）
  单独排入后续任务，不在阶段 8 内。**不要**把"默认值改成 780×540"当成"用户尺寸可保存恢复"。

## 1. 阶段 8 的四步（按优先级排序）

| 步骤 | 名称 | 主要执行方 | 当前状态 |
|---|---|---|---|
| **8.0**（前置） | 产物与代码对齐：提交 UI 改版 → 重跑两条发布路径 → 刷新 `publish/vm-package/` 的 exe | 我（需授权提交/发布） | ✅ **已完成（2026-10-27，见 §2.1）** |
| **8.1** | 跨设备与重启一致性验证 | 用户（虚拟机上跑） | ✅ **已完成并二次复验**：最新构建上 Phase 1 13/0、Phase 2 10/0 全绿（证据 `docs/stage8-step3-evidence/vm-run2-20261027/`） |
| **8.2** | 补齐发布兼容性（两版并存 + 缺运行时行为） | 我（本机）+ 用户（干净机确认） | 产物已刷新并实机复验；剩中文系统报错文案确认 |
| **8.3** | 数据与异常保护闭环 | 我 | ✅ **已完成（2026-10-27）**：补「导出诊断包」，冒烟 311/311 |
| **8.4** | 边界显示测试（DPI / 分辨率 / 副屏） | 用户改虚拟机设置 + 我记录 | 100%/125% 已过，150%/副屏未测 |

## 2.1 8.0 完成记录（2026-10-27）

| 项 | 值 |
|---|---|
| UI 改版提交 | `44a6306`「UI 改版：轻玻璃风格（阶段 2-5 全部完成）」 |
| 便携版 `publish/win-x64/DeskSpace.exe` | 519,773 B，MD5 `aa8d8505c36ef800ecc8b4695faaaeca` |
| 自包含版 `publish/win-x64-selfcontained/DeskSpace.exe` | 65,064,093 B，MD5 `b8f3e6ef1ad71b280ec87b44953c29aa` |
| `publish/vm-package/DeskSpace.exe` | 已刷新，MD5 与自包含版一致 |
| `publish/vm-package-portable/DeskSpace.exe` | 已刷新，MD5 与便携版一致 |
| 实机复验 | 两个 exe 各自启动 → 主窗口可见 → 全屏截图存于 `docs/stage8-80-evidence-portable/`、`docs/stage8-80-evidence-selfcontained/`，**截图确认显示的是改版后轻玻璃界面** |
| 文档订正 | `docs/install-and-uninstall.md` §已知限制第 1 条（干净环境报错已验证）、第 5 条（coreclr.dll 判据错误 → 改用 wpfgfx_cor3.dll） |
| 安装包内脚本 | `install.ps1` / `uninstall.ps1` / `stage8-step3-check.ps1` / `vm-check.ps1` 与 `tools/` 当前版本一致，无需刷新 |

### 8.3 后再次重发布（2026-10-27 11:48，含「导出诊断包」）

| 项 | 值 |
|---|---|
| 便携版 `publish/win-x64/DeskSpace.exe` | 527,965 B，MD5 `ef884a571a14e84ff080c6d385fb2138` |
| 自包含版 `publish/win-x64-selfcontained/DeskSpace.exe` | 65,067,641 B，MD5 `7a26f62872789d2dd11bf20e0ec9758c` |
| 新验收包 `publish/vm-package-20261027/` | 自包含 exe + install/uninstall/stage8-step3-check/vm-check + 说明，8.4 测试请用这个 |

**注意**：`publish/vm-package/` 里的旧 exe 当时正被一个运行中的 DeskSpace 实例占用（PID 266052），
没有强杀用户进程，改为新建一个带日期的包。等该实例关闭后再把 `vm-package` 刷新一次即可。

**推论：从现在起，8.4 的显示测试可以基于最新构建进行，结论有效。**

## 2. 8.0 为什么是最高优先级（必须最先做）

`publish/win-x64/` 与 `publish/win-x64-selfcontained/` 里的 exe 都是 **2026-10-25 06:01** 的产物，
而 10-26 的 UI 改版（16 个文件修改 + 4 个新增）**尚未提交、尚未发布**。
现在的实际状态是：**任何人安装拿到的都是改版前的界面**。

推论：**8.1 的虚拟机实测如果基于旧 exe，结论不算数**——验证的是旧界面、旧行为的跨设备一致性。
所以顺序必须是：提交 → 重发布 → 刷新 vm-package → 再跑 Phase 1/2。

动作清单（工作量小）：

1. 提交 UI 改版（`git add` + `git commit`，中文提交信息用文件方式避免编码问题）
2. 重跑两条发布路径（`win-x64-singlefile.pubxml` / `win-x64-selfcontained.pubxml`）
3. 刷新 `publish/vm-package/`、`publish/vm-package-portable/` 里的 exe 与 MD5
4. 复核 `docs/install-and-uninstall.md` 两处过时表述（§已知限制第 1 条、第 5 条 coreclr.dll 判据）

## 3. 8.1 跨设备与重启一致性

- 工具：`tools/stage8-step3-check.ps1`（18,661 B，两段式 Phase 1 → 重启 → Phase 2）
- 覆盖：A 自启写 Run 项 / B 图标隐藏→恢复 / C 异常强杀后状态保持 + `errors.log` 不增长 /
  D Explorer 重启后状态保持 / F 重启后自启生效 / G 禁用删 Run 项 / H 卸载清理含图标恢复
- 本机自检：**9 PASS / 2 FAIL**（两个 FAIL 是预期内的，证明判据有效）
- 待办（需用户在自己的虚拟机上执行，脚本会改 HKCU Run、动桌面图标、重启 Explorer）：
  新 exe → Phase 1 → 重启 → Phase 2 → 回传 `stage8-step3-report.txt` + 4 张截图
- 验收口径：窗口模式、项目库、桌面图标状态、设置四项在重启后都能按预期恢复

## 4. 8.2 发布兼容性

已具备：

- `win-x64-selfcontained.pubxml`（自包含）与 `win-x64-singlefile.pubxml`（便携，framework-dependent）并存
- `tools/install.ps1 -Edition auto|portable|selfcontained` + `-SourceExe`，按体积识别版本
- 验收脚本 `tools/verify-edition-install.ps1` **40 项 0 失败**
- 干净环境（Win11 虚拟机，无 .NET 10 桌面运行时）已验证：自包含版双击可跑；
  便携版弹 .NET 宿主标准报错框 + Download it now（**不静默失败**）

待补：

- ~~UI 改版后**重新发布并重新验证**两个版本~~ → **已完成**（见 §2.1）
- 报错框在**中文系统**上的文案确认（虚拟机是英文系统，之前只见到英文框）
- ~~`docs/install-and-uninstall.md` 两处过时表述订正~~ → **已完成**（见 §2.1）

## 5. 8.3 数据与异常保护（2026-10-27 全部完成）

| 子项 | 状态 | 证据 |
|---|---|---|
| 全局未捕获异常兜底 | **已完成** | `App.xaml.cs`：Dispatcher + AppDomain + TaskScheduler 三处 → `%LocalAppData%\DeskSpace\logs\errors.log` + 友好提示。虚拟机 C 段实测：强杀后 errors.log 11→11 不增长 |
| **原始配置保护（损坏时不覆盖）** | **已完成** | `Core/Services/JsonStore.cs`：JSON 损坏 / 版本过新 / 旧版迁移时原文件移入 `backups/`（`stem-<tag>-<时间戳>.json`），并记录警告；写入为原子 `.tmp` + `.bak` 再替换 |
| 关键数据迁移回归测试 | **已完成（当前版本口径）** | `DataVersions.Current = 1`；冒烟 §11 覆盖损坏文件 / 空文件 / Version 999 / Version 0 迁移 / 缺 Version 字段。**升到 v2 时必须同步补用例** |
| 诊断日志 | 已完成 | `sync.log`（512 KB 上限）+ `errors.log` |
| **导出诊断包** | **已完成** | 见下 |

### 导出诊断包（8.3 唯一缺口，已补齐）

- 新增 `src/DeskSpace.Core/Services/DiagnosticExporter.cs`（零第三方依赖，只用框架自带的
  `System.IO.Compression`）：打包 `settings.json`、`library.json`、`logs/*.log`，外加一份
  `manifest.txt`（导出时间、目录位置、系统/.NET 版本、备份清单、跳过项、隐私提示）。
- 日志文件以 `FileShare.ReadWrite` 打开 → **应用正在写日志时也能导出**，不会互相锁死。
- 备份目录只记名字不打包内容，避免诊断包被撑大；单文件超过 8 MB 也只记名字。
- 入口：设置窗口新增「诊断与排障」分区 + 「导出诊断包…」按钮（沿用现有分区/按钮样式，
  未引入新的视觉语言），导出到桌面 `DeskSpace-诊断包-<时间戳>.zip`，导出前弹确认并**明确告知
  包内含个人文件路径**。
- 测试：冒烟新增第 16 节共 19 条断言（空目录导出、内容完整、日志被占用、重复导出覆盖、
  非法路径返回失败、备份只记清单）→ **冒烟 292 → 311，全部通过**；解决方案构建 0 警告 0 错误。
- 取证截图：`docs/stage8-83-evidence-settings.png`（滚到底，可见「卸载前清理」与
  「诊断与排障」两个分区及「导出诊断包…」按钮）。

### 踩坑记录（后续开发注意）

- zip 条目名必须用**正斜杠**：`Path.Combine("logs", name)` 在 Windows 上给的是反斜杠，
  打包出来不会被标准解压工具识别为目录（冒烟第 16 节抓到 3 个 FAIL 后修正）。
- `UseWPF=true` 时隐式 using 不含 `System.IO`，新文件要显式写。
- 截图取证时，窗口里 TextBox 模板自带 ScrollViewer（`PART_ContentHost`），
  按"第一个"找会滚错对象；且滚动后要等一帧渲染再截屏。

## 6. 8.4 边界显示测试

| 场景 | 状态 |
|---|---|
| DPI 100% | ✅ 虚拟机 1100×720 @(370,136) 中心点在屏内 |
| DPI 125% | ✅ 虚拟机 1375×900 / 本机 775×650；本轮 UI 改版截图也全在 125% 下完成 |
| DPI 150% / 200% | ❌ 未测（已知限制） |
| 副屏停靠 / 混合 DPI 多屏 | ❌ 未测（本机多显示器但未逐一验证副屏停靠） |
| 悬浮窗 / 侧边栏在 100% 与 150% | ❌ 待补（用户建议项） |
| Acrylic 回退分支 / 远程会话回退 | ❌ 本机不可达（Acrylic 在本机不渲染），仅代码审查 |

无法实际验证的场景一律写入已知限制，**不得宣称已通过**。

### 8.4 待执行的操作清单（需用户在虚拟机上做，约 10 分钟）

前置：虚拟机里装的是阶段 8.0 重发布的自包含版（65,064,093 B）。每次改缩放后**注销再登录**
或重启一次，让 DPI 真正生效。

1. **150% 缩放**：设置 → 系统 → 显示 → 缩放改 150% → 重启虚拟机 → 依次打开
   ① 全屏工作空间 ② 悬浮窗（托盘菜单「悬浮窗模式」）③ 侧边栏（「侧边栏模式」），
   每个模式截一张图，重点看：文字有没有被截断、窗口有没有跑出屏幕、
   侧边栏**收起态**（点顶栏「收起」）是否还完整贴在屏幕边缘。
2. **100% 缩放**：同样三模式各一张（此前只验证过全屏与侧边栏收起态，悬浮窗与展开态待补）。
3. **改分辨率**：1080p → 1366×768（或更低）再跑一次三模式，确认小屏下窗口不会被挤变形。
4. **副屏停靠（如有多显示器）**：把窗口拖到副屏，切到侧边栏模式，确认停靠边跟着主/副屏
   位置走而不是贴在错误屏幕边缘；若两块屏缩放不同（混合 DPI），再看一次文字清晰度。

回传：把截图发回来即可，我据结果更新本表与 `docs/ui-redesign/report.md` 第 6 节的
"未验证项"，已通过的就从已知限制里移除。
