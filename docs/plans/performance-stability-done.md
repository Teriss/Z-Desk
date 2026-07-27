# 性能与稳定性排查

## 背景与目标

用户反馈 Z-Desk 使用时经常导致系统卡顿并发生崩溃。本计划聚焦高频事件、UI 线程阻塞、异步重入和异常恢复，降低持续 CPU/IO 占用并避免后台异常终止进程。

## 范围与非目标

- 范围：Shell/文件变更通知、布局刷新、规则执行、窗口定时器、日志和异步异常。
- 非目标：不改变文件操作的 Shell 语义，不进行无关 UI 或架构重构，不加入外部监控服务。

## 实施步骤

- [x] 定位高频事件、阻塞调用和崩溃风险，并记录实际调用链。
- [x] 修复确认的性能/稳定性问题，补充低成本回归测试或诊断保护。
- [x] 执行构建和冒烟测试，检查差异并记录人工验收项。
- [x] 同步用户文档中的已知限制和验证结果，完成计划归档。

## 涉及模块或文件

- `MainWindow.xaml.cs`
- `Controls/GroupContainer.xaml.cs`
- `Services/*` 中与通知、规则、日志和窗口宿主相关的实现
- `tests/ZDesk.SmokeTests`

## 验证方法

- `dotnet build ZDesk.csproj -c Debug`
- SmokeTests 自包含构建并运行
- `git diff --check`
- 人工观察文件大量变更、规则执行、边缘隐藏和 Explorer 重启场景

已完成验证：Debug/Release 构建和自包含 SmokeTests 均通过。尚未自动覆盖 Windows 实机上的 WorkerW、Explorer 重启、多显示器 DPI 和高频文件变更视觉行为。

## 风险或待确认事项

- Windows Shell COM、WorkerW 和低级钩子行为需要 Windows 10/11 实机人工验收。
- 本地环境若无法启动 WPF 或 Explorer 集成，只能完成编译和单元/冒烟级验证。
