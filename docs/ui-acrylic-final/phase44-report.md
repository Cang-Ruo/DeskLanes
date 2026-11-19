# 阶段 44：合并收敛光晕（阶段 43 候选 → 正式）

用户看完阶段 43 新旧对照后确认合并。

## 合并内容

- `EdgeStripWindow.xaml`：阶段 42 的三层 76 DIP 同心圆光晕
  （`OrbAuraIdle/Hover/Drag` + `OrbAura*Brush`）→ **单层 50 DIP 收敛光晕**
  （`OrbAura` + `OrbAuraTintBrush` 方向渐变 + `OrbAuraMaskBrush` 径向遮罩）。
- `EdgeStripWindow.xaml.cs`：三态强度收敛到 **Idle 2% / Hover 10% / Drag 17%**。

与候选 exe（`1213CB00…`）逻辑完全一致；正式构建 MD5 `066B03B1…` 不同
仅因合并版注释文本改为「阶段 44」。

**未改**：球体视觉、交互状态机、44/46/56/76 DIP 尺寸、长按 350ms、
位移 6 DIP、Tooltip 550ms、位置保存、多屏钳制、快捷键、其它窗口。

## 回归

| 项 | 结果 |
|---|---|
| Release 构建 | 0 警告 0 错误 |
| 冒烟 | **590 / 590**（新增第 45 节 4 条收敛光晕断言） |
| 启动探针 | 7 / 7 |
| 三屏端到端 | 连跑两次 **37 / 37** |
| 阶段 31 收束 | 8 / 8 |
| 阶段 34 形态 | 19 / 19 |
| 阶段 35 失焦 | 9 / 9（首轮 5/9 是探针链把 `FloatingHideOnDeactivate` 污染成 False，恢复真实值后复跑全过） |
| 收起球验收 | **13 / 13** |
| 三模式焦点一致性 | 0.0 / 0.0 / 0.0 |
| 安装 | `066B03B1…`，安装副本 MD5 一致 |

配置已恢复：模式 0、`FloatingHideOnDeactivate=True`、尺寸 504.56×369.24、
小球位置 2079/951。

## 交付

- 对照证据：`phase43-compare-{idle,hover,drag}.png` 与
  `phase43-candidate-report.md`（保留作合并依据）。
- 合并后实机截图：探针重跑生成，覆盖 `phase38-orb-*.png`（交互回归用）。
- 本次完成后继续暂停，未改侧边栏 / 全屏把手。
