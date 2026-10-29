# 阶段 9.10：竖屏收口（主窗口还原态自动收缩）+ 修「最大化时丢掉副屏位置」

日期：2026-10-30（夜间自主执行）
改动：`src/DeskLanes.App/MainWindow.xaml.cs`、`tools/verify-floating-interaction.py`（新增 Round F）
验收：构建 0 警告 0 错误；冒烟 **423/423**；端到端 **23/23 → 28/28**（新增 Round F 5 项）；启动实测 7/7。

---

## 1. 起因

竖屏适配在 9.5 做完了三个层面，留了一条记录里写着"锦上添花、以后再说"的残留：

> 主窗口在竖屏上「还原成普通窗口」时默认 1180 宽仍放不下（可以手动缩小到 620），
> 没有做"按屏自动选默认尺寸"。

本轮把它做掉。做的过程里又撞出一个**更严重**的缺陷，一并修了。

## 2. 主窗口按屏自动收缩

复用 9.5 已有的 `MonitorService.FitIntoWorkArea`（按显示器句柄取 DPI，只缩不放），
在两个时机触发：

| 时机 | 为什么 |
|---|---|
| `Loaded` / `Activated` 之后 | 窗口真正落到某块屏上以后句柄才定位。**不能**放在 `SourceInitialized`——那一刻还没定位，取到的是错的显示器 |
| 用户从最大化还原（`OnStateChanged`） | 还原会把尺寸弹回 1180，在 720 DIP 宽的竖屏上必然溢出 |

### 关键设计：收缩值不落盘

如果直接收缩，关闭时会把 1180 记成 712，**下次回到大屏还是个小窗口**——这是个比原问题更烦的回归。
所以把「用户想要的大小」和「收缩后实际显示的大小」分开记：

- `_intendedWidth/Height`：收缩前记录（来自配置或 XAML 默认值）
- `_fittedWidth/Height`：收缩后的实际值
- `OnClosing`：只有当我们确认"用户没有再手动改过大小"（当前尺寸 ≈ 收缩值）时，
  才存 `_intended*`；用户自己调过就以当前值为准

端到端 Round F 最后一条就是在锁这个行为：`WindowWidth` 仍然是 **1180**，没有被改成 712。

## 3. 顺带撞出并修掉的真缺陷：最大化时丢掉副屏位置

Round F 第一次跑挂了两条，根因不在收缩逻辑，而在 `ApplyWindowPreferences`：

```csharp
if (settings.WindowMaximized)
{
    WindowState = WindowState.Maximized;
    return;      // ← 位置偏好直接被丢掉
}
```

后果：**用户上次在副屏最大化，下次启动会在主屏最大化**——
窗口没设位置就走 `CenterScreen`，落在鼠标所在的那块屏上。
这正是用户上一轮要验的那条（"把主窗口挪到副屏后重启看位置记住没有"），之前没测出来，
因为 Round E 用的是 `WindowMaximized=False`（靠 `BeginFullMode` 最大化，位置就生效了）。

修法：**先把位置落下去再最大化**（WPF 是按"当前所在那块屏"决定最大化到哪的，
顺序反了就变成"先在主屏最大化、再把坐标设到副屏"，照样错位）：

```csharp
if (WindowState == WindowState.Maximized) { return; }   // 已经最大化（BeginFullMode 先跑过），别再动
// 尺寸 → 位置 → 最后才最大化
```

## 4. 端到端 Round F（新增 5 项，全过）

模拟用户点还原按钮（发真实 `WM_SYSCOMMAND / SC_RESTORE`，不是程序里直接改状态）：

```
== Round F: main window restored (normal) on the portrait monitor ==
PASS   starts maximized on the portrait monitor -- showCmd=3 at (-1091,-101)
PASS   restore button puts the window back to normal -- showCmd=1
PASS   restored window stays on the portrait monitor -- x=-984
PASS   restored window auto-shrinks to fit the portrait monitor -- width 1068 px physical (monitor 1080; unfixed would be ~1770)
PASS   shrunk size is NOT persisted (intended 1180 kept for the big screen) -- 1180
```

对照数据：

| 场景 | 修前 | 修后 |
|---|---|---|
| 副屏最大化后重启 | 在主屏最大化（位置偏好被丢） | **在副屏最大化**（-1091,-101） |
| 竖屏上点还原 | 1180 DIP ≈ 1770 物理像素，溢出约 690 | **1068 物理像素**（竖屏 1080） |
| 关闭后配置里的宽度 | — | **仍是 1180**，回到大屏不变小 |

## 5. 未做

- **"按屏自动选默认尺寸"** 没做（首次使用、没有任何偏好记录时，根据所在屏挑一个初始尺寸）。
  现在的做法是"先用默认值再收缩"，效果等价；区别只是收缩发生在窗口已经出现之后，
  理论上会有一帧是超宽的。实机没观察到闪烁，故不再增加复杂度。
