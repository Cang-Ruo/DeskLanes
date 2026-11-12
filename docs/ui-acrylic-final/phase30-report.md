# 阶段 30：失焦玻璃彻底解决（侧边栏改分层窗口）

用户反馈阶段 29 之后「还是会」——非焦点时侧边栏依旧是灰白实底。
本轮先做实验定位，再改成**由 WPF 自己合成透明像素**的分层窗口，
并给出「有焦点 / 无焦点」两态像素级一致的客观证据。

视觉证据：`phase30-sidebar-unfocused.png`（非活动态）、
`phase30-sidebar-focused.png`（活动态）。取证工具：`tools/phase30-focus-glass.py`。

## 一、为什么阶段 29 无效

阶段 29 在焦点变化时把 `DWMWA_SYSTEMBACKDROP_TYPE` 重新下发成 Acrylic，
日志里 `reapply on focus change hr=0x00000000` 确实触发、DWM 也接受。
但 **Win11 DWM 是按「窗口是否活动」这个状态决定要不要画纯色的**，
应用侧反复申请同一个材质改变不了这个状态机。用户实测证实仍然变灰。

先在真实窗口上做了两个实验：

| 实验 | 结果 |
|---|---|
| 对非分层窗口关系统 backdrop、改 `SetWindowCompositionAttribute` 老式 Acrylic | API 返回成功、画面像素差 30.33，但**只是中灰变深灰**，两张都看不到壁纸 —— Win11 上这条路对非分层窗口无效 |
| 把窗口改成分层窗口（`AllowsTransparency=True`） | 非活动态**能透出壁纸**（截图左下可见壁纸亮斑），文字仍清晰 |

结论：要让 DWM 没有机会按焦点换底色，只能让窗口自己画透明像素。

## 二、实现

- `SidebarWindow.xaml`：改回 `AllowsTransparency="True"`、
  `Background="#01000000"`（alpha≠0，分层窗口下鼠标才不会穿透），
  移除 `WindowChrome`（分层窗口不兼容）。
- `DwmInterop`：新增 `SetWindowCompositionAttribute` 封装
  （`WCA_ACCENT_POLICY=19`、`ACCENT_ENABLE_ACRYLICBLURBEHIND=4`、
  tint 用 AABBGGRR `0x18FFFFFF`），`TrySetLayeredAcrylic` 对无效句柄 /
  API 缺失都安全返回 false。
- `WindowBackdropService`：分层窗口走 `ApplyLayeredWindow` ——
  先把系统 backdrop 关成 `DWMSBT_NONE`（杜绝失焦纯色），再开窗口级模糊；
  焦点变化时重新确认这两步。非分层窗口（主窗口 / 悬浮窗 / 设置窗口）
  仍走原来的系统 Acrylic 路径。
- **回退保证**：即使窗口级模糊 API 不可用，XAML 根 Border 的半透明玻璃底
  也由 WPF 自己合成，两态一样透出壁纸（只是没有毛玻璃模糊）。

## 三、一个必须说明的取舍：悬浮窗没有一起改

一开始把悬浮窗也改成了分层窗口，结果**合成鼠标的拖拽 / 八方向缩放变得不稳定**：
端到端回归从 36/36 掉到 31~35/36，连跑两次失败项还不一样
（拖拽不移动、左边缘不缩放、窗口沉到桌面层被点不到）。
分层窗口的 alpha 命中测试会把 `Fill=Transparent` 的热区判成穿过，
即使把热区改成 `#0C000000`、把 `HWND_BOTTOM` 换成 `HWND_NOTOPMOST` 仍不稳。

这是产品行为退化，不能用改断言掩盖，所以**回退悬浮窗到阶段 29 的非分层实现**，
只让侧边栏分层（用户这轮截图里的窗口）。回退后端到端**连跑两次 36/36**。

> 代价：**悬浮窗失焦时仍可能变灰**（DWM 系统材质的限制）。
> 若要它也两态一致，需要把拖动条与八个缩放热区从「透明像素命中」
> 重构成统一的窗口级鼠标坐标判定，属于独立一轮改造，本轮没有动。

## 四、客观验证：两态像素差 0.0

`tools/phase30-focus-glass.py` 用 UiSnapshot 起一个
`ShowActivated=false` 的侧边栏（临时配置、不碰真实设置），
先在**非活动态**做 GDI 屏幕合成截图，再激活它在**活动态**截同一矩形：

```
first_state_unfocused : true
second_state_focused  : true
mean_pixel_diff       : 0.0
verdict               : 两态背景一致
```

即背景在两态下逐像素相同，不再有「灰白 vs 透明」的跳变。
两张截图同时展示了失焦态确实透出壁纸、文字仍清晰。

## 五、回归与交付

| 项目 | 结果 |
|---|---|
| Release 构建 | **0 警告 0 错误** |
| 冒烟 | **539 / 539**（新增 2 条：窗口级 Acrylic 对无效句柄安全失败、Win32 常量正确） |
| 启动探针 | **7 / 7**，`errors.log` 未增长（4810 → 4810 字节） |
| 三屏端到端 | **连跑两次 36 / 36** |
| 发布 | selfcontained `AD42663A5D1C2AB68C8081806B9D07E3`、portable `79D00F819B1C37C3D4C2074A2C5C5227` |
| 安装 | `install.ps1 -Edition selfcontained -Force` 覆盖安装，安装副本 MD5 一致 |

为替换 exe 结束过运行中的实例，请重新按 `Win` → `DeskLanes` 启动，
悬浮窗用 `Ctrl+Alt+Space` 呼出。

## 六、仍需用户实测 / 未闭环

1. 真机上侧边栏失焦是否还变灰（客观证据是两态像素差 0.0，但请以实际观感为准）。
2. 侧边栏改成透明后，亮壁纸下的白字可读性（名字牌有 38% 黑 scrim，纯文字标题没有）。
3. **是否也要让悬浮窗两态一致**——需要独立一轮重构热区，告诉我你的选择。
4. 长期未闭环：物理热插拔复测、真实睡眠唤醒、阶段 14 之后新构建 24h+ 驻留、
   远程桌面会话；Windows 更新后的 explorer.exe DLL 报错是否消失。
