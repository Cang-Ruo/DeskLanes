# DeskSpace 阶段 2 报告：分类、自由分区与自定义添加

> 日期：2026-10-21 · 基于阶段 1（已验收）代码扩展，未重写既有功能

---

## 1. 本阶段目标与完成情况

| 目标 | 状态 |
|---|---|
| 两种全屏布局（分类导航式 / 自由分区式）共用同一份数据，切换不丢数据 | ✅ 已实现并验证 |
| 数据模型区分"桌面扫描事实信息"与"用户自定义信息" | ✅ 已实现并验证 |
| 分类增删改排序 / 项目归属调整 / 空分类状态 | ✅ 已实现并验证 |
| 分区增删改 / 位置尺寸 / 顺序 / 项目进出 / 布局持久化 | ✅ 已实现并验证 |
| 自定义添加（文件选择器：程序、快捷方式、文件夹、普通文件） | ✅ 已实现并验证 |
| 失效路径保留配置并显示失效状态 | ✅ 已实现并验证 |
| `%LocalAppData%\DeskSpace\` 安全保存（原子写入、损坏/空/版本迁移处理、先备份） | ✅ 已实现并验证 |
| 不得移动/删除/修改原始文件，不得伪装分类 | ✅ 全部测试实证 |

**构建结果**：`dotnet build DeskSpace.sln` → **0 警告 0 错误**（net10.0-windows，WPF + .NET 10 LTS）。
**测试结果**：冒烟测试 **118/118 通过**（阶段 1 的 36 项全部保留并通过）。

---

## 2. 新增/改动的文件

### Core 层（数据与逻辑）

| 文件 | 说明 |
|---|---|
| `Models/DesktopItem.cs`（重写） | Id 改为稳定 GUID；新增 `PathKey`（全路径大写，扫描匹配键）、`IsAvailable`、`AddedAt`/`LastSeenAt`、`Origin.Custom`、`StatusText`；`Icon` 及全部计算属性标 `[JsonIgnore]`；Name/Kind/Origin 等可被扫描刷新 |
| `Models/WorkspaceModels.cs`（新增） | `LayoutMode` 枚举、`ShortcutGroup`（分类）、`WorkspacePanel`（分区，虚拟画布 1600×900 + 几何钳制常量）、`AppSettings`、`LibraryData`、`DataVersions` |
| `Models/IVersionedData.cs`（新增） | 版本检查契约 |
| `Services/JsonStore.cs`（新增） | 通用 JSON 存储：原子写入（.tmp → File.Replace 带 .bak）、损坏自动备份到 `backups/`、空文件按首次使用、版本过新备份后重置、版本过旧备份后迁移 |
| `Services/LibraryStore.cs` / `ConfigStore.cs`（新增） | 语义化包装（library.json / settings.json） |
| `Services/DesktopItemFactory.cs`（新增） | 扫描器与自定义添加共用同一套"事实信息"构造逻辑 |
| `Services/WorkspaceService.cs`（新增） | 扫描合并（PathKey 复用稳定 Id）、分类/分区全部操作（单成员制）、自定义添加、移除（扫描项记忽略列表防回归）、布局模式、窗口偏好；`Changed` 事件驱动 UI 刷新，`SaveFailed` 事件提示写入失败 |
| `Services/ServiceContracts.cs`（扩展） | `IWorkspaceService` 接口；`IDialogService` 增加 Confirm/Prompt/PickFile/PickFolder |
| `Services/DesktopScanner.cs`（小改） | Id 生成移交 WorkspaceService（合并时按 PathKey 复用），CreateItem 改用共享工厂 |

### App 层（MVVM + WPF）

| 文件 | 说明 |
|---|---|
| `ViewModels/MainViewModel.cs`（重写为外壳） | 扫描调度、布局切换、自定义添加、启动分发、状态栏；保留阶段 1 全部公开 API |
| `ViewModels/ShellState.cs`（新增） | 跨布局共享的搜索词 |
| `ViewModels/CategoryNavViewModel.cs`（新增） | 左导航右网格；分类 CRUD/排序/归属/空状态/分类内搜索 |
| `ViewModels/FreePanelsViewModel.cs`（新增） | 分区面板（稳定实例，不整体重建）+ 未分区项目池 + 归属操作 |
| `Views/CategoryNavView.xaml(.cs)`（新增） | 左侧分类列表（拖放目标）+ 右侧项目网格 + 移除区 |
| `Views/FreePanelsView.xaml(.cs)`（新增） | 工具栏 + 虚拟画布（ScaleTransform 缩放）+ 项目池 + 移除区 |
| `Views/PanelView.xaml(.cs)`（新增） | 分区面板：标题栏拖动移动、右下角手柄缩放、项目区拖放 |
| `Views/ItemTile.xaml(.cs)`（新增） | 项目磁贴：图标/名称/失效徽标/拖拽源 |
| `Views/TextInputDialog.xaml(.cs)`（新增） | 单行文本输入（分类/分区命名） |
| `Views/BindingProxy.cs`、`Views/DragDropFormats.cs`（新增） | ContextMenu 绑定中转、拖拽数据格式 |
| `MainWindow.xaml(.cs)`（重写为外壳） | 标题/搜索/警告条/布局切换/ContentControl 装载当前布局/状态栏；窗口偏好恢复与保存 |
| `App.xaml.cs`（小改） | 组装 LibraryStore/ConfigStore/WorkspaceService |

### 测试与工具

| 文件 | 说明 |
|---|---|
| `tests/DeskSpace.SmokeTests/Program.cs`（扩展） | 阶段 1 全部保留；新增第 8-11 节：工作区服务、持久化重启、子视图模型、配置容错，共 118 项 |
| `tests/DeskSpace.UiSnapshot/`（新增） | 界面快照工具：用真实数据渲染两种布局并截图（只读真实配置的副本） |
| `docs/stage2-category-nav.png`、`docs/stage2-free-panels.png` | 两种布局的真实渲染截图（见第 6 节） |

---

## 3. 关键设计决策

1. **成员关系存容器侧**：`Group.ItemIds` / `Panel.ItemIds` 持有项目 Id，项目对象无反向引用。删除分类/分区只删容器记录，项目自动回到未分组/未分区池——桌面文件零触碰。
2. **稳定 Id + PathKey 匹配**：扫描合并时按全路径（大写）匹配复用旧 Id，成员关系跨扫描保留；同一项目不会因改名/重扫产生重复条目。
3. **单成员制**：一个项目同一时刻只属于一个分类或一个分区（拖到新容器自动离开旧容器），与"两种布局是同一份数据的两种视图"的定位一致。
4. **移除 ≠ 删除**：扫描项被用户移除后记入 `DismissedPaths`，重扫不会拉回（防"移除后又出现"）；文件始终留在磁盘上。自定义项移除只删库内记录。
5. **失效保留**：磁盘上消失的项目标记 `IsAvailable=false` 留在库中，成员关系与配置不丢，UI 显示"已失效"徽标。
6. **原子写入**：先写 `.tmp` 再 `File.Replace`（保留 `.bak`），崩溃不会产生半截配置；损坏配置自动备份到 `backups/` 后恢复默认值并警告。
7. **虚拟画布 1600×900 + ScaleTransform**：分区几何用虚拟单位存储，渲染时按窗口大小等比缩放（居中），不同窗口尺寸下布局一致；拖拽时按实时缩放系数换算增量。
8. **稳定视图模型实例**：分区面板 VM 按模型 Id 缓存复用，不在每次变更时整体 Clear 重建，避免 ItemsControl 容器反复销毁/重建导致的渲染错乱与选中丢失。

---

## 4. 实测记录（验收 8 项）

> 以下全部为真实执行，未执行的项目在第 5 节明确标注。

### 4.1 冒烟测试（`dotnet run --project tests/DeskSpace.SmokeTests`）

**118/118 通过**，覆盖：

- **分类 CRUD**：新建排序、重命名、删除（只删记录、项目与文件保留）、上移、加入/移出/跨分类调整（单成员制实证）
- **分区 CRUD**：新建、标题、删除、位置尺寸更新、越界几何钳制（X=0、最小宽度）、上移、项目进出/跨分区调整
- **重启后布局保存**：同一目录新建服务实例（模拟重启）→ 项目数量、分类、分区、分区几何、成员关联、布局模式、忽略列表全部保留；`library.json` 与 `.bak` 均在
- **布局切换数据一致**：MainViewModel 切换后搜索词保留、面板数据保留；分类导航中的归属调整在自由分区池中立即可见（同一份底层数据）
- **添加外部项目**：普通文件、文件夹添加成功且 `Origin=Custom`；不存在路径返回中文错误；空路径拒绝；重复添加幂等
- **移除后源文件仍在**：移除扫描项后 `File.Exists` 实证为 true，重扫不回归；移除自定义项同理
- **重启后关联仍在**：分类/分区成员关系、失效项目关联重载后保持
- **无效路径与损坏配置**：磁盘消失项标记失效且保留在库；损坏 JSON → 备份 + 默认值 + 警告；空文件按首次使用；版本过新（v999）→ 备份 + 默认值 + 警告；旧版（v0）→ 备份 + 迁移 + 按当前版本加载；缺 Version 字段宽容加载；settings.json 损坏同样容错；手工编辑的中文配置可正常读回

### 4.2 真实应用运行（`DeskSpace.exe`，真实桌面）

- 启动成功：扫描到 **74 个项目**（用户桌面 49 + 公共桌面 25，18 文件夹 / 49 快捷方式 / 7 文件），与阶段 1 数据一致
- `%LocalAppData%\DeskSpace\` 生成 `library.json`（36KB，稳定 GUID、无计算属性污染）+ `settings.json`；第二次启动幂等（合并后内容一致），`.bak` 备份正确生成
- 优雅关闭（taskkill WM_CLOSE）：窗口偏好（位置 434,172）正确写回 settings.json
- 全程无错误日志（`logs/errors.log` 不存在）、无未处理异常、进程正常退出

### 4.3 界面渲染（`DeskSpace.UiSnapshot` 工具，真实数据）

- 分类导航式：左导航（未分组/工作/常用工具 + 计数）+ 右侧真实图标网格 + 移除区 + 状态栏，渲染正确
- 自由分区式：两个分区按各自几何渲染，内容互不串扰，缩放手柄可见，项目池计数正确
- 截图存档：`docs/stage2-category-nav.png`、`docs/stage2-free-panels.png`

---

## 5. 诚实声明（未执行/未验证的部分）

1. **鼠标拖拽与右键菜单未做端到端实测**：拖放/菜单的代码路径（DragDrop、ContextMenu、Thumb 拖动）已编写并构建通过，归属逻辑已被 118 项测试完整覆盖（VM 层），但**没有对真实窗口做模拟鼠标操作的端到端测试**（环境无 UI 自动化工具）。首次真实使用时请重点验证：拖动项目到分类/分区、右键"打开/移除"、分区标题栏拖动与右下角缩放。
2. **`OpenFolderDialog` 编译通过但未实际弹出**：文件选择器走 `Microsoft.Win32.OpenFileDialog/OpenFolderDialog`，编译器验证类型存在，未在运行中点击"添加项目"按钮验证弹窗。
3. **布局切换按钮、搜索框在真实窗口中的点击行为**未逐一点击（视图模型层已全部测试）。
4. **裸 Win 键**仍按约定未采用；全局快捷键属阶段 4 范围，本阶段未触碰。
5. **多显示器场景**未实测（开发机为多显示器 + 虚拟显示器环境，但快照与应用运行均在主屏完成）。

---

## 6. 已知问题与后续建议

1. **画布高度受限时项目池被压缩**：窗口较小时，画布（保持 1600×900 宽高比）会占据大部分高度，项目池可用空间很小。后续可给项目池设置最小高度、画布设置最大高度，或允许项目池折叠。
2. **分类/分区很多时的导航**：左侧列表与画布都没有滚动以外的导航优化，后续可加搜索定位、画布缩放到适配（"适应全部面板"按钮）。
3. **配置版本迁移框架已就绪但只有一个版本**：未来加字段时按现有 `Version` 机制递增即可，迁移逻辑已实证可用。
4. **UI 自动化测试缺失**：建议阶段 3 起引入（如 FlaUI）以覆盖拖拽等交互。

---

## 7. 下一步（等待确认后继续）

按既定路线图，阶段 3 建议方向：模式切换体验完善（悬浮窗/全屏/侧边栏的窗口行为）、FileSystemWatcher 实时刷新、全局快捷键。请确认后继续；如需调整阶段 2 的任何行为（例如移除确认策略、空分类提示文案、画布默认尺寸），也请在继续前提出。
