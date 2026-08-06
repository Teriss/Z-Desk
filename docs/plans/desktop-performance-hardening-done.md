# 桌面布局性能加固

## 背景与目标

修复大目录下图标网格未真正虚拟化、桌面 Shell 通知重复刷新以及文件集合逐项移动造成的性能隐患，同时保持现有视图、Shell 文件语义和持久化格式不变。

## 范围与非目标

- 范围：虚拟化换行面板、FileEntry 按需加载、批量集合刷新、桌面通知合并与性能回归测试。
- 非目标：不引入第三方依赖，不修改 `layout.json` 格式，不替换 Windows Shell 文件操作，不改变用户可见的八种视图。

## 实施步骤

- [x] 新增虚拟化 `VirtualizingWrapPanel`，并让图标、小图标、平铺视图使用它。
- [x] 将图标和元数据加载改为可视项目按需启动，补齐排序时的元数据等待。
- [x] 用批量集合替换逐项 `IndexOf`/`Move`，保留选择、焦点和条目对象复用。
- [x] 限制 Shell 通知到用户/公共桌面，合并 FileSystemWatcher 与 Shell 刷新去抖。
- [x] 增加 500 项目录、容器回收、通知合并和批量刷新回归测试。
- [x] 更新架构文档，执行 Debug/Release 构建和 SmokeTests；记录待实机人工验收项。

## 涉及模块或文件

- `Controls/VirtualizingWrapPanel.cs`、`Controls/GroupContainer.xaml`、`Controls/GroupContainer.xaml.cs`
- `Models/FileEntry.cs`、`Services/DesktopFileService.cs`、`Services/ShellChangeNotificationService.cs`
- `MainWindow.xaml.cs`、`tests/ZDesk.SmokeTests/Program.cs`、`docs/ARCHITECTURE.md`

## 验证方法

- 500 项映射目录在图标视图中只创建可视区加缓存行的容器，滚动和键盘定位正常。
- 文件创建、删除、重命名和排序保留选择/焦点且不会重复启动条目加载。
- Shell 与 watcher 的连续事件只触发一个协调批次，空闲后不持续刷新。
- 执行仓库规定的 Debug 构建、自包含 SmokeTests、Release 构建和 `git diff --check`。
- Windows 10/11 实机人工验收 Explorer 重启、多 DPI、拖放、框选、重命名和退出恢复桌面图标。

已完成自动验证：Debug/Release 构建、自包含 SmokeTests（含 500 项虚拟化容器和批量 Reset 回归）均通过。尚未完成 Windows 实机上的 Explorer 重启、Shell 通知范围、多 DPI 和连续文件变更视觉验收，因此计划暂不归档为 `-done`。

## 风险或待确认事项

- WPF 自定义虚拟化面板涉及 `IScrollInfo`、键盘定位和容器回收，需重点测试滚动、框选和拖放。
- Shell PIDL 注册和 Explorer 重启恢复必须在 Windows 10/11 实机验证。
