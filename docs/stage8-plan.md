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
| **8.1** | 跨设备与重启一致性验证 | 用户（虚拟机上跑） | **基本完成（2026-10-27）**：Phase 2 十项全过 + 4 张截图；Phase 1 文字报告曾被覆盖（脚本已改为分阶段写文件），A–E 文字证据如需可重跑 Phase 1 补 |
| **8.2** | 补齐发布兼容性（两版并存 + 缺运行时行为） | 我（本机）+ 用户（干净机确认） | 产物已刷新并实机复验；剩中文系统报错文案确认 |
| **8.3** | 数据与异常保护闭环 | 我 | 大部分已做，剩"导出诊断包" |
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

**推论：从现在起，8.1 的虚拟机实测可以基于新 exe 进行，结论有效。**

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

## 5. 8.3 数据与异常保护（缺口比预想小）

| 子项 | 状态 | 证据 |
|---|---|---|
| 全局未捕获异常兜底 | **已完成** | `App.xaml.cs`：Dispatcher + AppDomain + TaskScheduler 三处 → `%LocalAppData%\DeskSpace\logs\errors.log` + 友好提示 |
| **原始配置保护（损坏时不覆盖）** | **已完成** | `Core/Services/JsonStore.cs`：JSON 损坏 / 版本过新 / 旧版迁移时原文件移入 `backups/`（`stem-<tag>-<时间戳>.json`），并记录警告；写入为原子 `.tmp` + `.bak` 再替换 |
| 关键数据迁移回归测试 | **已完成（当前版本口径）** | `DataVersions.Current = 1`；冒烟 §11 覆盖损坏文件 / 空文件 / Version 999 / Version 0 迁移 / 缺 Version 字段。**升到 v2 时必须同步补用例** |
| 诊断日志 | 已完成 | `sync.log`（512 KB 上限）+ `errors.log` |
| **导出诊断包** | **未做（唯一缺口）** | `src/` 下检索"导出/zip/Export"零命中。建议：一键打包 `settings.json` + `library.json` + `logs\` 为 zip 放桌面，用 `System.IO.Compression`，零第三方依赖 |

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
