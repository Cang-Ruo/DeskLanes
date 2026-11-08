# 阶段 11：三屏端到端 36/36，以及运行中换壁纸自动重算

> 一句话结论：**阶段 10 那 4 项「真实几何问题」结论是错的**——A/B 证明是测试脚本的前置状态
> 污染（`WindowWidth` 被上一轮写成 2062.4，而断言假定 1180），产品定位逻辑一行没改，
> 归一化后三屏端到端 **36/36**。另外补上了运行中换壁纸自动重算：不重启应用，
> 暗→亮切换后 16 块文字全部翻到浅底深字，像素对比度最低 **5.11**，静置 0 次闪烁。

证据：`phase11-evidence.json`、`phase11-wallpaper-refresh{,-reverse}.json`、
五张重拍的基线截图与 `env.json`。含真实桌面项目名的 PNG 按 `.gitignore` 不入库。

---

## 一、4 项竖屏断言：翻案

阶段 10 记的是「保存位置 `WindowLeft=-800` 未生效、窗口最大化落在主屏，属真实几何问题，
建议单独阶段修窗口定位逻辑」。本阶段先做只读 A/B，把唯一的变量 `WindowWidth` 改回断言
假定的 1180 DIP：

| 配置 | 最大化后矩形 | `showCmd` | `MonitorFromWindow` |
|---|---|---:|---|
| 当前 `WindowWidth = 2062.4` DIP | `(-9,-9) 2578×1398` | 3 | 主屏 `(0,0,2560,1440)` |
| 归一化 `WindowWidth = 1180` DIP | `(-1091,-11) 1102×1870` | 3 | **左竖屏** `(-1080,0,0,1920)` |

原因很清楚：`WindowLeft=-800` 其实一直生效，但窗口被之前的轮次撑到 2062.4 DIP
（= 2578 物理像素）后，从 x=-1000 一直延伸到 x=1578，**中心点落在主屏**，
WPF 就把它最大化到主屏；窄到 1180 DIP 时中心点回到竖屏，最大化自然落在竖屏。
Round C / F 的断言 `WindowWidth == 1180` 正是这个前置条件——它俩失败也是同一污染。

**修法只动测试脚本**（`tools/verify-floating-interaction.py`）：

- 开头把主窗 `1180×760`、悬浮窗 `780×540`、位置与 Maximized 归一化到确定基线；
- 末尾把运行前的 `settings.json` **原封不动写回**。以前不还原，Round D 会把「竖屏收缩后的
  712 DIP」永久留在用户配置里（阶段 10 已经踩过一次）——现在跑完配置仍是用户的
  `2062.4 / 712 / -847.2`；
- 不改任何判据，不改产品的一行定位逻辑。

结果：三屏端到端 **32/36 → 36/36，0 失败**，且用户偏好未被改写。
阶段 10 报告与验收报告里相关表述已同步订正。

> 教训：多显示器几何断言必须在**确定的配置基线**上跑；把「窗口尺寸」也算进基线，
> 否则窗口中心点决定 `MonitorFromWindow`，看起来就像定位逻辑坏了。

---

## 二、运行中换壁纸自动重算

### 为什么不能继续抓屏

阶段 10 的亮度网格是在 `App.OnStartup`、任何窗口 Show 之前抓的一帧纯桌面。
运行中换壁纸时窗口是可见的，再抓屏就会把自己的文字与图标一起采进去。
以前的做法是「如实记为限制：换壁纸需重启」，本阶段把它补上。

### 实现

- `DesktopLuminance.SetFromWallpaper()`：从 HKCU 读当前壁纸文件，用 WPF 自带的
  `BitmapDecoder` / `FormatConvertedBitmap`（**零第三方依赖**）解码，
  枚举 `EnumDisplayMonitors` 得到每块屏的物理矩形，按 **Fill（等比裁剪居中）**
  把壁纸映射到每块屏，逐 32px 网格取亮度，再做与抓屏路径相同的 3×3 模糊。
  虚拟屏里没有显示器的空隙填 0，与真实抓屏帧一致。
- `DynamicTextService.OnEnvironmentChanged()`：`WM_SETTINGCHANGE` / `WM_DISPLAYCHANGE`
  触发，1.5 秒合并去重（签名 = 壁纸路径 + 网格数），成功后把亮度来源切到
  `wallpaper_bitmap` 并 `RequestRefresh()`。解码失败时**保留原网格、不假装更新过**。
- `AppController.OnHostMessage` 增加 `WM_SETTINGCHANGE` 分支（不设 `handled`，
  不抢别的监听者），`WM_DISPLAYCHANGE` 分支顺带调用重算。
- 诊断 JSON 增加 `luminance_source` / `desktop_refreshes`，便于自证到底用的哪一帧。

### 实测（`tools/phase11-wallpaper-refresh.py`，**不重启应用**）

| 方向 | 亮度来源 | 重算次数 | 深字方案 | 像素对比度 p50 / 最低 |
|---|---|---:|---:|---:|
| 暗→亮 前 | `screen_capture` | 0 | 1 | 10.67 / 7.66 |
| 暗→亮 后 | **`wallpaper_bitmap`** | **1** | **16** | **6.77 / 5.11** |
| 亮→暗 前 | `screen_capture` | 0 | 16 | 6.77 / 5.11 |
| 亮→暗 后 | **`wallpaper_bitmap`** | **1** | 4 | 10.67 / **5.03** |

两个方向都满足全部 5 条判据：来源切换、重算次数增加、方案变化、像素最低 ≥ 4.5、
静置后 0 次翻转。暗→亮后的 `6.77 / 5.11` 与阶段 10 **启动抓屏**测出的亮壁纸数值
一字不差，说明 Fill 映射是可靠替代。

切换瞬间每块文字各翻转一次（16 块 → flips +15）是**预期的一次性响应**，不是闪烁；
静置后 `flips` 不再增长。亮→暗后深字方案是 4 而不是启动抓屏时的 1，
是壁纸位图按每屏 Fill 映射与 DWM 实际合成在多屏宽高比不同时的近似差异，
两者都让所有文字 ≥ 4.5，不作为缺陷。

---

## 三、回归与交付

| 项目 | 结果 |
|---|---|
| Release 构建 | **0 警告 0 错误** |
| 冒烟 | **460 / 460** |
| 端到端（三屏） | **36 / 36** |
| 启动 | **7 / 7**，热中位 1.45 s，`errors.log` 未增长 |
| 玻璃透光 | 空玻璃格 **0.616 / 不透明 −0.001 → PASS** |
| 动态文字像素回归 | p50 **6.77 / 最低 5.11**（固定浅字 2.11 / 1.93），静置 0 翻转 |
| 五张基线截图 | 最终构建重拍，5/5 活动可见、每例清场 6 个窗口、`BACKDROP=3`、侧栏 `CORNER=1` |
| 发布 | portable `89235dca…`（574,557 B）、selfcontained `8efec54d…`（65,094,159 B） |
| 安装同步 | `install.ps1 -Edition selfcontained -Force`，安装副本与发布副本 **MD5 一致**；开机自启与桌面图标隐藏未改动 |

---

## 四、改动文件

- `tools/verify-floating-interaction.py`：测试几何归一化 + 末尾还原用户配置。
- `src/DeskLanes.App/Services/DesktopLuminance.cs`：`CurrentWallpaperPath()`、
  `SetFromWallpaper()`、显示器枚举与 Fill 映射、公共 `Blur()`。
- `src/DeskLanes.App/Services/DynamicText.cs`：`OnEnvironmentChanged()`、
  1.5 秒合并与签名去重、诊断字段 `luminance_source` / `desktop_refreshes`。
- `src/DeskLanes.App/Services/AppController.cs`：新增 `WM_SETTINGCHANGE`，
  `WM_DISPLAYCHANGE` 顺带触发重算。
- 新增 `tools/phase11-wallpaper-refresh.py`（支持 `--reverse`）。
- 订正 `phase10-report.md`、`acceptance-report.md` 中已过时的结论。

**产品行为变化**：只有「运行中换壁纸 / 改显示器拓扑后文字颜色会跟着更新」这一项；
启动路径、窗口几何、材质、配置结构、快捷键都没动。

---

## 五、复现

```bash
# 三屏端到端（会归一化几何基线，跑完还原用户配置）
python tools/verify-floating-interaction.py

# 运行中换壁纸（暗→亮 / 亮→暗），不重启应用
python tools/phase11-wallpaper-refresh.py
python tools/phase11-wallpaper-refresh.py --reverse

# 回归
dotnet run --project tests/DeskLanes.SmokeTests -c Release --no-build
python tools/measure-startup.py
python tools/phase10-dynamic-proof.py --wall=light
```
