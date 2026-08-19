# 修复图标视图点击后滚动位置跳回顶部

## 背景与目标

图标、平铺和小图标视图使用 `VirtualizingWrapPanel`。点击下方项目时，WPF 的自动定位回调会把滚动位置跳回顶部。目标是保持鼠标点击时的当前滚动偏移，同时保留键盘和程序主动定位。

## 范围与非目标

- [x] 修正自定义虚拟化面板的真实容器索引计算。
- [x] 在鼠标项目选择期间抑制 `MakeVisible` 和 `BringIndexIntoView`。
- [x] 删除项目点击后的多余焦点定位；空白区域框选继续获取焦点。
- [x] 增加自定义面板的 SmokeTest 回归覆盖。
- [x] 完成 Debug/Release 构建、自包含 SmokeTests 和差异检查。
- [ ] 在 Windows 10/11、多 DPI 和多显示器环境中人工验收。

## 涉及模块

- `Controls/VirtualizingWrapPanel.cs`
- `Controls/GroupContainer.xaml.cs`
- `tests/ZDesk.SmokeTests/Program.cs`

## 验证方法

- 图标、平铺和小图标视图滚动到底部后点击项目，滚动偏移不变化。
- 键盘导航和程序主动定位仍将目标滚动到可视区。
- 验证鼠标框选、拖拽、Ctrl+A、Enter 和列表/详细信息/内容视图不回归。

## 风险与待确认

鼠标选择抑制在输入循环结束后解除，避免影响键盘可访问性；真实 WPF 输入和多 DPI 行为仍需 Windows 实机验收。
