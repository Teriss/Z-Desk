# 崩溃与首次右键性能修复

## 背景与目标

修复跨布局拖拽后 `PinnedPaths`/`ItemOrder` 不一致导致的重复键崩溃，并降低每次启动后首次打开原生 Shell 右键菜单时加载扩展造成的 UI 卡顿。

## 范围与非目标

- 范围：普通布局项目归属与顺序、即时规则分类、历史状态清洗、异常日志、Shell 菜单预热和高频详细日志。
- 非目标：不替换 Windows 原生菜单，不禁用第三方 Shell 扩展，不移动真实文件，不默认生成内存转储。

## 实施步骤

- [x] 统一普通布局项目添加、移除和状态规范化，修复即时分类重复归属。
- [x] 加载时自动清洗历史状态，并为字典构建增加防御性去重。
- [x] 在后台 STA 线程预热一次不可见 Shell 菜单，正式菜单继续使用 UI 所有者窗口。
- [x] 精简高频日志并记录完整异常堆栈。
- [x] 增加回归测试，执行 Debug/Release 构建和自包含 SmokeTests。

## 涉及模块或文件

- `Controls/GroupContainer.xaml.cs`
- `MainWindow.xaml.cs`
- `Services/LayoutStore.cs`
- `Services/ShellContextMenuService.cs`
- `Services/LogService.cs`
- `tests/ZDesk.SmokeTests/Program.cs`

## 验证方法

- 重现“应用程序 -> 给木 -> 锁定给木 -> Shell 通知”，确认项目不重复且进程不退出。
- 验证异常历史状态加载后顺序去重、残留清理、缺失项补齐。
- 验证菜单预热不显示菜单、不执行命令且只运行一次。
- 运行规定的 Debug/Release 构建、SmokeTests 和 `git diff --check`。

## 风险或待确认事项

- 第三方 Shell 扩展本身可能缓慢或挂起；预热线程必须为后台 STA，且不能阻止应用退出。
- 原生菜单所有者绘制仍需 Windows 10/11 实机人工验收。

已完成 Debug/Release 构建和自包含 SmokeTests。首次菜单耗时及第三方扩展兼容性保留为实机人工验收项。
