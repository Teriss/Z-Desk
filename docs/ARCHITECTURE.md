# Z-Desk 架构说明

## 1. 进程与窗口

`App` 负责单实例、退出事件、创建布局事件和异常记录。`MainWindow` 是隐藏控制器，不绘制桌面，负责状态、服务编排和窗口集合。

每个 `GroupDefinition` 对应一个 `DesktopGroupWindow`；窗口内容为 `GroupContainer`。普通模式通过 WorkerW 边界保持在普通应用之后，置顶模式使用 `WS_EX_TOPMOST`。布局间的前后顺序在 Z-Desk 窗口集合内部调整。

## 2. 数据模型

```text
AppState
├─ AppSettings
├─ GroupDefinition[]
│  └─ LayoutTab[]
├─ LayoutMatchRule[]
├─ ClassificationRule[]
└─ DesktopIconPlacement[]
```

- `GroupDefinition` 保存宿主位置、尺寸、折叠、锁定和布局级设置。
- `LayoutTab` 保存页签 ID、类型、文件路径顺序、视图和排序状态。
- 多页签宿主不是额外布局；每个页签仍是独立布局实体。
- `LayoutMatchRule.GroupId` 指向普通布局或普通页签。
- `FileEntry` 只缓存真实路径的元数据和 Shell 图标；条目进入可视区后才启动对应的 Shell 加载任务，图标换行视图由回收式虚拟化面板限制容器数量。
- `AppSettings.TopmostHotKeys` 保存多条置顶快捷键；旧版单快捷键在 `LayoutStore` 载入时迁移为“全部布局”绑定。
- `AppSettings.QrRecognitionHotKey` 保存二维码识别的单条全局快捷键；空值表示不注册该功能。
- `LayoutMatchRule.PathContains` 与扩展名同时参与匹配；快捷方式目标由 `ShortcutTargetService` 解析。
- `AppSettings.InteractionMode` 控制标准吸附或 QQ 式贴边隐藏，`GroupDefinition.DockEdge` 保存左、右、上停靠边。

## 3. Explorer 集成

### 桌面层

`WorkerWHostService` 定位 Explorer 的桌面 WorkerW。`DesktopGroupWindow` 通过 Win32 Z 序和 `WM_WINDOWPOSCHANGING` 约束窗口层级，不创建覆盖全屏的透明画布。

### 桌面图标

`DesktopIconVisibilityService` 控制 Explorer `SysListView32` 的可见性。启动时隐藏，正常退出恢复；watchdog 等待主进程退出后恢复异常状态。

### 双击隐藏

`DesktopDoubleClickService` 使用低级鼠标钩子，并验证命中窗口属于 Explorer 桌面根窗口；ListView 命中测试排除桌面图标。其他应用和布局窗口不会触发。

### Shell 菜单

布局内文件通过 `ShellContextMenuService` 调用 `IContextMenu`，并向 `IContextMenu2/3` 转发所有者绘制和子菜单消息。跨目录多选通过 `IShellItemArray` 获取统一菜单，避免静默丢弃选中项。Win11 第一层桌面菜单由 `native/ZDeskExplorerCommand` 提供，依赖独立 MSIX/COM 扩展。

### 选择与预览

WPF 维护布局内的选中项和焦点项。普通单击、框选、`Ctrl+A` 和右键选择不导航 Explorer。`DesktopShellSelectionService` 只在按空格预览真实桌面项目时同步隐藏的桌面 ListView；映射项目不支持这种同步。

`FilePreviewService` 通过 `IFilePreviewProvider` 适配第三方预览程序。内置 QuickLook Provider 将焦点项真实路径传给本地安装版、运行中实例、WindowsApps 执行别名或商店版激活入口。其他预览工具必须单独实现 Provider，不能依赖 WPF 选中状态被系统自动识别。

## 4. 交互事件

`GroupContainer` 产生拖动、缩放、页签、文件操作和布局菜单事件；`MainWindow` 负责修改 `AppState`、更新其他窗口并排队保存。布局窗口的置顶状态由快捷键目标和贴边悬停状态的并集决定。

文件系统操作由 `IShellFileOperationService` 在独立 STA 线程调用 `IFileOperation`，包括重命名、复制、移动、粘贴和批量回收站删除；冲突、权限、进度和取消由 Windows Shell 处理。双击和 Enter 使用 `ShellExecuteEx`。

拖放分为三类：同一普通布局内只修改 `ItemOrder`；普通布局之间只改变布局归属，不移动真实桌面文件；映射布局拖入根据 OLE 效果调用 Shell Copy/Move。拖出使用标准 WPF OLE `IDataObject`，同时提供 `CF_HDROP` 和内部布局格式，可与 Explorer 及其他支持文件拖放的应用交互。

Shell 操作完成和 `FileSystemWatcher` 通知都按路径增删或替换 `FileEntry`。桌面目录的 Shell 通知仅注册用户桌面和公共桌面，并与 watcher 汇入同一个去抖批次；通知溢出或缺少详细路径时执行完整桌面核对。列表刷新使用批量 Reset，保留未变化条目、选择和焦点，因此文件改名不会逐项重排布局内容或窗口尺寸。

布局页签拖出时先从宿主移除；释放到原宿主恢复原索引，释放到其他布局合并，释放到空白处保留为独立窗口。

二维码识别由 `QrRecognitionFrameController` 管理一个可复用的 `QrRecognitionFrameWindow`：同一窗口承载 VS Code 风格标题栏、尺寸文本、识别/关闭按钮和连续边框。标题栏负责移动，八向透明命中区负责缩放，取景区域通过 `WM_NCHITTEST` 中央透明命中穿透到背后应用。快捷键不捕获或冻结桌面，外层窗口边界与内部物理捕获边界分开计算，跨屏移动始终使用物理坐标。窗口不设置 `WDA_EXCLUDEFROMCAPTURE`，因此远程桌面软件可以显示取景框；点击识别后先隐藏窗口，再调用 `ScreenCaptureService.CaptureRegion`，避免把自身 UI 截入图像。

点击“识别”或按 Enter 后，控制器隐藏取景框并调用 `ScreenCaptureService.CaptureRegion`，按显示器交集复制选区，显示器间隙填白，不创建虚拟桌面尺寸缓冲。捕获和 `QrCodeRecognitionService` 解码在后台执行；识别服务使用 ZXingCpp，仅扫描 QR Code，并对原图、对比度拉伸灰度和各颜色通道执行局部/全局二值化回退，再按定位范围合并同一二维码的重复命中。捕获异常时恢复取景框以便重试；结果窗口只提供正文复制，不执行或联网解析二维码内容。

## 5. 持久化

- `LayoutStore`：`layout.json`、备份、版本校验和损坏恢复。
- `SnapshotService`：布局快照。
- `DisplayLayoutProfileService`：显示器组合配置。
- `RuleHistoryService`：规则执行历史。
- `RecoveryService`：会话异常标记。
- `LogService`：按日期日志与保留策略。
- 二维码取景框边界属于用户设置，保存为 `AppSettings.QrRecognitionFrameBounds`；屏幕图像和二维码正文不是持久化数据，只在一次识别会话内驻留内存。
- `AppDataPathService`：路径配置、目录迁移和运行时服务切换。

路径指针保存在 `HKCU\Software\ZDesk`。迁移先复制，成功后切换路径，旧目录保留为备份。

## 6. 首次启动与规则

无 `layout.json` 时，`LayoutStore` 创建一个大尺寸组合宿主和九个默认页签（含游戏），并将 `LayoutMatchRule` 绑定到对应页签。MainWindow 创建窗口后执行一次规则预览和归类，只改变布局归属，不移动文件。路径条件按项目路径、快捷方式目标和 URL 进行不区分大小写的关键词匹配。

## 7. 发布边界

| 产物 | 内容 | Win11 第一层桌面菜单 |
| --- | --- | --- |
| 便携 EXE | 单文件核心程序 | 不部署 |
| 单文件便携版 | 核心程序 `ZDesk.exe` | 不部署 |

正式分发需要对便携 EXE 签名。核心功能不依赖 MSIX；MSIX/COM 仅作为可选开发集成保留。

## 8. 测试

`tests/ZDesk.SmokeTests` 覆盖 Shell 重命名/复制/移动、QuickLook Provider、映射选择隔离、规则属性通知、二维码单码/多码/同文重复/旋转/反色/风格化样例识别、v13→v14 配置迁移、取景框默认尺寸/最小尺寸/夹取/工具条定位和区域捕获几何、500 条规则页 Dispatcher 响应、备份恢复、视图/排序/页签、窗口 Z 序、自动折叠、首次初始化、路径迁移和非模态设置同步。取景框穿透、八向缩放、跨 DPI 和真实视频画面仍需人工验收。

## 9. UI 主题

设置中心采用左侧单列导航和右侧卡片内容区。通用颜色与控件样式由 `Resources/SettingsTheme.xaml` 及设置窗口资源提供；深色界面不得使用 WPF 默认白底控件或系统图标字体。新增界面必须遵守 [UI 设计规范](UI_STYLE.md) 并完成多 DPI、交互状态和 Win10/Win11 验收。
二维码识别使用 `QrRecognitionFrameController` 管理可复用取景框。进入模式不捕获桌面；识别时 `ScreenCaptureService.CaptureRegion` 只复制物理选区与显示器交集，空隙填白，随后在后台调用 ZXingCpp。
