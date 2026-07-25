# Z-Desk

Windows 桌面布局管理器。布局窗口位于 Explorer 桌面层，文件操作直接作用于真实路径，不替换或注入 Explorer。

## 功能

- 普通布局、文件夹映射布局、组合布局与多页签。
- 拖动、缩放、跨显示器、边缘吸附、重叠布局按交互置前。
- 标准吸附与 QQ 式贴边隐藏、双击桌面空白隐藏。
- 可配置多条全局快捷键，分别置顶指定布局或全部布局。
- Shell 原生右键菜单、重命名、属性、回收站删除、复制/移动、拖放和 QuickLook。
- 多选、框选、`Ctrl+A`、视图模式、排序和规则自动归类。
- 首次启动创建默认组合布局及九个分类页签（含游戏）。
- 自动归类支持扩展名与路径关键词 AND 匹配，可识别 `.lnk`/`.url` 目标。
- 桌面新增文件在开启“桌面新增项目时应用规则”后自动归类；规则编辑支持“规则 / 文件夹 / 其他文件”类型，文件夹和其他文件类型不显示可编辑条件。
- 布局选择不会打开 Explorer 或改变文件夹窗口；真实桌面项目按空格时静默同步桌面选择，映射项目由 QuickLook Provider 直接预览真实路径。
- 快捷方式显示名隐藏 `.lnk`/`.url` 后缀；未经 Provider 适配的第三方预览工具无法自动读取 WPF 自绘选中项。
- 设置非模态实时同步；数据目录和日志目录可迁移。

## 技术栈

| 项目 | 方案 |
| --- | --- |
| UI | WPF / .NET 10 |
| 桌面层 | Win32 WorkerW、窗口 Z 序、Per-Monitor V2 DPI |
| 文件交互 | Windows Shell `IFileOperation`、`IContextMenu2/3`、OLE/CF_HDROP、Shell 图标、FileSystemWatcher |
| 系统集成 | 全局热键、低级鼠标钩子、托盘、Explorer 图标 watchdog |
| Win11 桌面菜单 | C++ `IExplorerCommand` + MSIX/COM |
| 发布目标 | `win-x64` 自包含 |

架构说明见 [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)。

其他文档见 [docs/README.md](docs/README.md)。

## 系统要求

- Windows 10 22H2 x64 或 Windows 11 x64。
- 开发：.NET 10 SDK。
- Release：Visual Studio C++ x64 Build Tools（构建 Win11 菜单扩展）。
- 不支持 Windows 32 位。

Windows 10 支持核心功能；Win11 第一层桌面菜单要求系统 build 22000+。

## 构建与测试

```powershell
dotnet build ZDesk.csproj -c Release
dotnet build tests\ZDesk.SmokeTests\ZDesk.SmokeTests.csproj -c Release -p:SelfContained=true -p:UseAppHost=true -o artifacts\ci-smoke
.\artifacts\ci-smoke\ZDesk.SmokeTests.exe
```

开发运行：

```powershell
dotnet run --project ZDesk.csproj
```

## 发布

单文件便携版：

```powershell
.\scripts\publish-portable.ps1
```

本项目仅分发单文件便携版 `ZDesk.exe`，不会安装额外服务或修改 Explorer；Win11 第一层菜单 MSIX 不包含在发布包中。布局内和布局自身的右键菜单不受影响。

当前 0.9.0 发布产物：

| 类型 | 文件 | 大小 | SHA-256 |
| --- | --- | ---: | --- |
| 单文件便携版 | `artifacts/portable/ZDesk-0.9.0-win-x64/ZDesk.exe` | 75,489,419 字节 | `FEB1366D9BC43533C77B1E6B79B99CB9B52EB6C0B60724DD061161B25A91FB43` |


## 数据

默认数据目录 `%LocalAppData%\ZDesk`，日志目录 `%LocalAppData%\ZDesk\logs`。设置中可修改，迁移采用复制并保留源目录备份。状态文件为 `layout.json`，保存前生成 `layout.backup.json`。

## 质量边界

自动测试覆盖 Shell 重命名/复制/移动、预览 Provider、选择隔离、500 条规则页性能、规则通知、持久化、页签、窗口层级、停靠模式、首次初始化、存储迁移和设置同步。Explorer 图标恢复、QuickLook 实际安装、回收站取消、跨应用拖放、多显示器 DPI、Win+D 和扩展菜单仍需在目标 Windows 版本人工验收。

Issue 和 PR 模板位于 `.github/`；提交问题请附 Windows 版本、复现步骤、版本类型和相关日志。
