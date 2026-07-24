# 贡献指南

## 环境

- Windows 10 22H2 或 Windows 11 x64
- .NET 10 SDK
- Release 构建 Explorer 扩展需要 Visual Studio C++ x64 Build Tools

## 构建与测试

```powershell
dotnet build ZDesk.csproj -c Release
dotnet build tests\ZDesk.SmokeTests\ZDesk.SmokeTests.csproj -c Release -p:SelfContained=true -p:UseAppHost=true -o artifacts\ci-smoke
.\artifacts\ci-smoke\ZDesk.SmokeTests.exe
```

提交前请确认没有把 `bin/`、`obj/`、`artifacts/` 或 C++ 中间文件加入提交。

## 提交变更

提交信息应说明行为变化和受影响的平台。涉及 UI、Shell、Explorer 或文件操作的改动，应补充对应的 SmokeTest 或人工验收步骤。
