# DeskSpace 发布准备检查（阶段 8 快照）

> 检查时间：2026-10-25 04:0x（自动化每周巡检）
> 范围：只检查与汇报，未修改任何代码
> 基线：`docs/project-overview-and-roadmap.md` §七 P1

## 0. 回归复核（本轮实测）

| 项目 | 结果 |
|---|---|
| Release 构建（DeskSpace.sln） | **0 警告 0 错误**，5 个项目，5.93s（增量，"所有项目均是最新的"） |
| 冒烟测试 | **289 / 289 全过**，失败 0（含第 14 节写 HKCU Run、第 15 节写 HideIcons，均自动还原） |

无回归。

## 1. 四项 P1 状态

| # | P1 项 | 状态 | 关键证据 |
|---|---|---|---|
| 1 | 自包含发布与便携版并存 | **进行中** | 配置已并存且路径分离；产物与安装脚本未跟上 |
| 2 | 干净环境 / 跨设备 + DPI 矩阵 | **未开始** | 无任何第二台机器证据；DPI 矩阵零记录 |
| 3 | 开机自启进真实验收脚本 | **未开始** | 验收脚本中相关关键字出现数为 0 |
| 4 | 可交付版本三项 + 三份文档 | **进行中** | 异常兜底已实现；导出诊断包未开始 |

## 2. 逐项证据

### P1-1 自包含发布（进行中）

已完成：
- `src/DeskSpace.App/Properties/PublishProfiles/win-x64-selfcontained.pubxml` 已存在
  （`SelfContained=true` / `PublishSingleFile=true` / `IncludeNativeLibrariesForSelfExtract=true` /
  `PublishTrimmed=false` / `DebugType=embedded`）
- 与 `win-x64-singlefile.pubxml` 输出目录分离：`publish\win-x64-selfcontained\` vs `publish\win-x64\`，
  文件头注释里有两份配置的对照表 —— **两条路径不会混淆**

未完成：
- `git status` 显示该 pubxml 为 `??`（未跟踪、未提交），有丢失风险
- `publish/` 下只有 `win-x64\DeskSpace.exe`（505,949 B），**没有自包含产物**，即从未真正发布过一次
- `tools/install.ps1` 第 42-49 行只认 `publish\win-x64\DeskSpace.exe`，**没有版本选择参数**；
  缺运行时时只打印下载链接并询问是否继续（第 82-88 行），不会自动切到自包含版

### P1-2 跨设备与 DPI 矩阵（未开始）

- `docs/stage7-fixes.md` §175：「真正未安装运行时的机器 —— 本机无法模拟，未验证，需要第二台机器或干净虚拟机」
- `docs/stage7-fixes.md` §191 / `docs/project-overview-and-roadmap.md` §118：
  仅本机验证（Win11 Build 26300、多显示器 + 虚拟显示器、DPI 1.25），无单显示器 / 标准 DPI / 不同缩放比
- `tools/verify-missing-runtime.ps1` 只是把 `DOTNET_ROOT` 指向空目录的**近似模拟**
  （实际仍回退到系统运行时正常启动），不等于真缺运行时
- `tests/DeskSpace.Acceptance/stage4-accept.ps1` 只在本机 DPI 1.25 环境跑过，无缩放矩阵

### P1-3 开机自启与图标隐藏交互（未开始）

- `tests/DeskSpace.Acceptance/*.ps1`（37 个脚本）中 `自启 / Startup / CurrentVersion\Run` 出现数 **0**
  —— 与 `docs/project-overview-and-roadmap.md` §119 的记录一致，本轮复核确认仍未改善
- 唯一覆盖是冒烟第 14 节的**注册表层**断言（本轮 PASS），不是端到端系统级验证
- 「正常退出 / 异常终止 / Explorer 重启」与桌面图标隐藏状态的交互：**无任何验证记录**

### P1-4 可交付版本三项与三份文档（进行中）

| 子项 | 状态 | 证据 |
|---|---|---|
| 全局未捕获异常兜底 | **已完成** | `src/DeskSpace.App/App.xaml.cs` 20-95 行：Dispatcher + AppDomain + TaskScheduler 三处兜底，写 `%LocalAppData%\DeskSpace\logs\errors.log` 并弹友好提示。注意：roadmap §七 P3-12 仍把它列为"建议做"，**基线文档该项已过时** |
| 关键数据迁移回归测试 | **基本完成** | `DataVersions.Current = 1`（`WorkspaceModels.cs` 286-291），现有路径全覆盖：损坏 / Version 999 过新回退 / Version 0 旧版 / 缺 Version 字段；11.5 节覆盖 `settings.json` 损坏 |
| 诊断日志 | 部分完成 | `FileDiagnosticLogger`（sync.log，512 KB 上限）+ errors.log；**无"导出诊断包"入口** |
| 导出诊断包 | **未开始** | `src/` 下检索"导出"零命中，界面无入口 |
| 安装说明 | 齐备 | `docs/install-and-uninstall.md` §安装 |
| 卸载说明 | 齐备 | 同文档 §卸载 |
| 已知限制说明 | 齐备（未独立成文） | 同文档 §已知限制 + roadmap §六 |

## 3. 当前阻塞点

1. **缺第二台机器 / 干净虚拟机** —— P1-2 硬阻塞，本机无法替代
2. self-contained pubxml **未提交**（`??`），且从未产出过产物
3. `install.ps1` 无版本选择，自包含版即使发布也装不上
4. 开机自启真实验收**需要用户授权**（会写 HKCU Run、可能要注销重登 / 重启 Explorer）
5. 导出诊断包未做（P1-4 最后一块拼图）

## 4. 下一步最值得做的动作

1. **收口 P1-1**：提交 selfcontained pubxml → 实际跑一次自包含发布拿到产物 → `install.ps1` 加版本选择参数。
   这是唯一不依赖外部资源就能完成的 P1，且直接消除"目标机没装运行时"这个最大分发风险。
2. **补导出诊断包**：一键打包 `settings.json` + `library.json` + `logs\`，工作量小，
   也是跨设备排障时的唯一信息来源（P1-2 一旦启动，没有诊断包就只能靠口头描述）。
