# DeskSpace UI 改版（轻玻璃风格）· 阶段 1：现状检查与改动计划

日期：2026-10-26

## 1. 现状检查结论

### 窗口与样式现状

| 文件 | 现状 | 关键事实 |
|---|---|---|
| `App.xaml` | `<Application.Resources>` **为空** | 没有任何共享样式；各窗口各自内联颜色/样式 → 需要新建统一资源字典（规格 3.4 允许"创建或整理"） |
| `MainWindow.xaml` | 1180×760，浅色 `#F5F6F8`，标准边框，不透明 | 行结构：标题按钮 / 全宽搜索框 / 警告条 / 内容 / 状态栏；搜索框在 MainWindow 层（跨导航列），导航列在 CategoryNavView 内部 |
| `CategoryNavView.xaml` | 左导航 **230 DIP 固定** + 右侧 WrapPanel 网格 | 规格建议 165～185 → 调整为 180 |
| `ItemTile.xaml` | 108×104，图标 44，名称 12，深色文字 | code-behind 只有拖拽逻辑，无颜色代码 → 纯 XAML 重样式安全 |
| `FreePanelsView.xaml` | 虚拟画布 1600×900（`#EEF1F5` 不透明），底部项目池 + 移除区 | 画布底色改成低透明度即可，几何/缩放逻辑不动 |
| `PanelView.xaml` | 白色面板 + Thumb 标题拖动 + 右下角缩放手柄 | 全部交互逻辑保留，只换配色 |
| `FloatingWindow.xaml` | 620×520，**已是** `WindowStyle=None + AllowsTransparency=True`（分层窗口） | 白色圆角卡片 + 阴影；尺寸未持久化 |
| `SidebarWindow.xaml` | 280 宽，分层窗口，白底，停靠侧自适应圆角/边框 | 收起态 MinWidth=44，展开按钮逻辑不能动 |
| `SettingsWindow.xaml` | 460×520，浅色，标准边框 | 全部设置项与绑定保留 |
| `WorkspaceModels.cs` | `FloatingWindowWidth/Height/Left/Top` 已定义但**全项目无任何使用**（死字段） | 印证规格"先确认现有配置结构"：悬浮窗尺寸持久化未接线 |

### 关键技术结论（规格 3.3 的评估）

1. **MainWindow / SettingsWindow 是普通（非分层）窗口** → 可用 Win11 22H2+ 的 DWM 系统背景
   （`DWMWA_SYSTEMBACKDROP_TYPE`，Acrylic=磨砂；失败退 Mica；再退纯色）。
   需要同时：`DwmExtendFrameIntoClientArea(-1)` 把玻璃延伸进客户区 + 窗口背景透明 +
   `DWMWA_USE_IMMERSIVE_DARK_MODE` 让标题栏文字变白 + `DWMWA_WINDOW_CORNER_PREFERENCE` 圆角。
2. **FloatingWindow / SidebarWindow 是分层窗口（AllowsTransparency=True）** →
   DWM 系统背景与分层窗口**不兼容**（DWM 合成不在 WPF 渲染树内）。
   → 按规格的回退路径：**高不透明度（约 90%）的玻璃底色画刷**（文字/图标保持全不透明），
   不动窗口分层结构（改普通窗口会引入原生标题栏/缩放边框，属于新窗口管理行为，规格明确不允许）。
   真实模糊对分层窗口需要未公开 API，本次不做（列入已知限制）。
3. **DWM 背景必须封装一次**（规格 3.3）：新增 `Core/Native/DwmInterop.cs` + `App/Services/WindowBackdropService.cs`，
   各窗口只调一个方法。回退链：系统版本/远程会话检查 → 写入背景类型 → 读回校验 → 失败则还原不透明底色，**窗口永远可见**。
4. **UiSnapshot 工具用 RenderTargetBitmap** → 拍不到 DWM 合成的毛玻璃（布局可验、玻璃需真机截图）。
   需要扩展该工具支持 floating/sidebar 两个窗口的截图（交付要求）。
5. **冒烟测试只触碰 ViewModel/服务**（292 项），XAML 重样式不影响它们，但每阶段仍全量回归。

### 基线（改动前）

- Release 构建：0 警告 / 0 错误（7.5s）
- 冒烟测试：**292 / 292 通过**
- 改版前截图：`docs/ui-redesign/before/full-category.png`、`full-panels.png`

## 2. 文件改动计划

### 纯 UI 改动（XAML + 资源）

| 文件 | 改动 | 目的 |
|---|---|---|
| `src/DeskSpace.App/Themes/GlassTheme.xaml` | **新建** | 统一颜色/透明度/字体/圆角/边框/按钮/搜索框/列表选中态/项目磁贴/滚动条/工具提示/菜单（规格 3.4） |
| `src/DeskSpace.App/App.xaml` | 合并 GlassTheme | 让所有窗口共享 |
| `MainWindow.xaml` | 玻璃化整体布局 | 全屏模式轻玻璃；绑定/命令/控件名不动 |
| `Views/CategoryNavView.xaml` | 导航列 230→180 + 玻璃样式 | 规格建议宽度；选中低透明度蓝 |
| `Views/ItemTile.xaml` | 图标 44→48、磁贴 112×108、名称 13 | 图标成为视觉焦点（规格 4.4）；hover 反馈 |
| `Views/FreePanelsView.xaml` | 画布底色低透明、面板玻璃化、池/移除区玻璃化 | 空间感（规格 4.3），几何逻辑不动 |
| `Views/PanelView.xaml` | 玻璃底色/描边/标题栏 | 统一面板视觉 |
| `Views/FloatingWindow.xaml` | 玻璃卡片 + 默认尺寸 620×520→**780×540** | 规格 5.1（持久化未接线，改默认值不影响既有配置；同步模型死字段默认值保持一致） |
| `Views/SidebarWindow.xaml` | 玻璃化，浅色文字 | 规格六 6.1，停靠/收起逻辑不动 |
| `Views/SettingsWindow.xaml` | 玻璃化 + 表单控件统一 | 规格六 6.2，设置项/默认值/保存行为不动 |
| `Views/TextInputDialog.xaml` | 跟随主题 | 小对话框统一 |

### 系统互操作改动（新增，封装一次）

| 文件 | 改动 | 目的 |
|---|---|---|
| `src/DeskSpace.Core/Native/DwmInterop.cs` | **新建** | DwmSetWindowAttribute / DwmExtendFrameIntoClientArea / 远程会话检测 |
| `src/DeskSpace.App/Services/WindowBackdropService.cs` | **新建** | ApplyGlass(window)：版本/远程会话检查 → Acrylic → 读回校验 → 逐级回退；两个窗口共用 |

### 测试工具改动（不影响应用行为）

| 文件 | 改动 | 目的 |
|---|---|---|
| `tests/DeskSpace.UiSnapshot/Program.cs` | 增加 `floating` / `sidebar` 两种快照模式 | 交付要求：三种窗口的实际截图 |

### 明确不改

- 所有 ViewModel、Core 服务、数据模型结构、配置结构、托盘、热键、开机自启、图标隐藏、安装/卸载。
- 窗口呼出/收起/切换行为；不新增动画；不新增主题切换。

## 3. 实施顺序（每阶段构建+冒烟+快照全绿才进下一阶段）

1. 阶段 2：GlassTheme + BackdropService + MainWindow 玻璃化
2. 阶段 3：CategoryNavView + ItemTile + FreePanelsView + PanelView
3. 阶段 4：FloatingWindow
4. 阶段 5：Sidebar + Settings + TextInputDialog + UiSnapshot 扩展 + 真机截图 + 报告

## 4. 阶段执行记录（全部完成）

- 阶段 2：完成。实机验证 Mica 渲染（Acrylic 本机不渲染，Mica 优先），产物 `phase2-category.png`、`live-glass-full.png`。
- 阶段 3：完成。导航列 180、磁贴 48/13、自由分区池区显示修复（既有缺陷），产物 `phase3-*.png`、`live-panels.png`。
- 阶段 4：完成。悬浮窗 780×540 玻璃化，模型死字段默认值同步，构建 0/0。
- 阶段 5：完成。详见 `report.md`。要点：
  - SidebarWindow / SettingsWindow / TextInputDialog 玻璃化；
  - 修复侧边栏空状态文案永显的既有缺陷（`SidebarViewModel` 缺 `HasNoItems` 属性，XAML 绑定落空）；
  - UiSnapshot 扩展四种模式（floating / sidebar / settings / dialog），settings/dialog 用 GDI BitBlt 屏幕合成截图验证真实 Mica；
  - 解决方案 Release 构建 0 警告 0 错误；冒烟测试 292/292 通过；
  - 实机截图（125% DPI）：悬浮窗、侧边栏、设置窗口、输入对话框；
  - 临时验证脚本清理，保留通用工具 `tools/live-mode-shot.ps1`。
