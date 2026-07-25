# Z-Desk AI 协作规则

本文件是仓库级持久指令，适用于整个项目。开始任何任务前，先阅读本文件；如果将来某个子目录存在更近的 `AGENTS.md`，同时遵守该文件对其目录树的补充规则。

## 接手项目

开始修改前必须先了解当前工作区，禁止覆盖或回退用户已有的未提交改动。

1. 运行 `git status --short --branch`，确认分支、未提交改动和未跟踪文件。
2. 阅读根目录 `README.md`，了解产品定位、平台要求、构建和测试入口。
3. 阅读 `docs/README.md`，以它作为项目文档索引。
4. 阅读 `docs/ARCHITECTURE.md`，了解进程、窗口、数据模型、服务边界、Explorer/Shell 集成和持久化设计。
5. 阅读 `CONTRIBUTING.md`，遵守构建、测试和提交要求。
6. 根据任务补充阅读相关文档：
   - UI、XAML、主题或交互改动：`docs/UI_STYLE.md`。
   - 用户操作、兼容性或行为改动：`docs/USER_GUIDE.md`。
   - 发布、版本或打包改动：`docs/RELEASE_CHECKLIST.md` 和当前版本发布说明。
   - 隐私、日志、诊断或用户数据改动：`docs/PRIVACY.md`。
7. 查看 `docs/plans/`。如果任务对应已有未完成计划，先阅读该计划并延续它，不要创建内容重复的计划。

不要只依赖文档推断实现；修改前继续定位并阅读实际调用链、相邻代码和相关测试。若实现与文档不一致，以确认后的实际行为为依据，并在本次变更中同步修正文档。

## 计划文档

当用户要求制定计划，或任务需要先形成多步骤实施计划时，必须把计划写入 `docs/plans/`，不能只在对话中给出临时计划。

- 文件名使用简短、可识别的 kebab-case 名称，例如 `docs/plans/shell-drag-drop.md`。
- 新计划不得使用 `-done` 后缀。
- 计划至少包含：背景与目标、范围与非目标、实施步骤、涉及模块或文件、验证方法、风险或待确认事项。
- 实施步骤使用 Markdown 任务列表，并在执行过程中及时更新状态。
- 执行已有计划前先重新核对计划与当前代码；必要时先更新计划，记录范围或设计变化。
- 未完成、受阻或仅部分完成时保留原文件名，并准确记录剩余工作，不得标记为完成。
- 只有计划范围内的实现、必要文档和约定验证都已完成后，才将文件重命名为 `<原名>-done.md`。例如：

  ```powershell
  git mv docs\plans\shell-drag-drop.md docs\plans\shell-drag-drop-done.md
  ```

- 已完成计划是项目历史记录。除非用户明确要求，不要删除，也不要在后续无关任务中继续追加新范围。

## 实现约束

- 项目是 Windows x64 上的 WPF / .NET 10 应用，同时包含 Win32、Windows Shell 和可选的 C++ Explorer 命令扩展。
- 遵循现有分层：数据结构放在 `Models/`，系统与业务能力放在 `Services/`，窗口放在 `Windows/`，可复用控件放在 `Controls/`，共享主题资源放在 `Resources/`。
- 保持 nullable 检查有效，复用现有服务接口和依赖关系；只有确有需要时才引入新抽象或依赖。
- Shell、文件操作、窗口层级、WorkerW、热键、DPI 和持久化改动属于高风险区域。修改时必须检查线程模型、COM/STA 要求、句柄生命周期、异常恢复以及 Windows 10/11 差异。
- 文件操作必须继续使用项目的 Shell 语义，不能用普通文件 API 静默替代回收站、冲突处理或跨应用拖放行为。
- UI 改动必须复用 `Resources/SettingsTheme.xaml` 和现有资源，遵守 `docs/UI_STYLE.md`；检查深色主题、键盘焦点、窗口最小尺寸和多 DPI 状态。
- 行为、配置格式、用户操作或架构发生变化时，同步更新对应文档。不要让 README、架构说明、用户指南和实际实现相互矛盾。
- 修改应聚焦当前任务；不要顺手重构无关代码、格式化整仓库或提交生成物。

## 验证

验证范围应与风险匹配。默认至少执行：

```powershell
dotnet build ZDesk.csproj -c Debug
dotnet build tests\ZDesk.SmokeTests\ZDesk.SmokeTests.csproj -c Debug -p:SelfContained=true -p:UseAppHost=true -o artifacts\ci-smoke
.\artifacts\ci-smoke\ZDesk.SmokeTests.exe
```

- 发布相关或可能受配置差异影响的改动，还要执行 `dotnet build ZDesk.csproj -c Release`。
- 便携版改动执行 `scripts/publish-portable.ps1`；正式发布完整遵循 `docs/RELEASE_CHECKLIST.md`。
- UI、Shell、Explorer、拖放、回收站、快捷键、多显示器和 DPI 等无法完全自动验证的行为，必须记录具体人工验收项和未验证原因。
- 纯文档改动可不运行应用构建，但要检查链接、路径、命令、命名规则以及 `git diff --check`，并在交付说明中明确验证范围。
- 测试生成的 `bin/`、`obj/`、`artifacts/` 和 C++ 中间文件不得提交。

## 完成与交付

完成前检查 `git diff` 和 `git status`，确认只包含任务范围内的改动，并说明已执行的验证及未覆盖项。只有用户明确要求时才创建提交或推送；提交信息应说明行为变化和受影响平台，不得声称未执行的测试已经通过。
