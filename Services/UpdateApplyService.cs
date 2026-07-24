using System.Diagnostics;
using System.IO;

namespace ZDesk.Services;

public sealed class UpdateApplyService
{
    public string PrepareRollbackScript(string verifiedPackagePath, string applicationPath)
    {
        if (!File.Exists(verifiedPackagePath)) throw new FileNotFoundException("已验证的更新包不存在。", verifiedPackagePath);
        var directory = Path.GetDirectoryName(applicationPath) ?? throw new InvalidOperationException("应用目录无效。");
        var backup = applicationPath + ".previous";
        var script = Path.Combine(directory, "apply-update.cmd");
        var content = $"""
            @echo off
            setlocal
            set "NEW={verifiedPackagePath}"
            set "APP={applicationPath}"
            set "OLD={backup}"
            for /L %%i in (1,1,20) do (
              move /Y "%APP%" "%OLD%" >nul 2>nul && goto replace
              timeout /t 1 /nobreak >nul
            )
            exit /b 1
            :replace
            move /Y "%NEW%" "%APP%" >nul 2>nul || goto rollback
            start "" "%APP%" --updated
            exit /b 0
            :rollback
            move /Y "%OLD%" "%APP%" >nul 2>nul
            start "" "%APP%" --update-rollback
            exit /b 2
            """;
        File.WriteAllText(script, content);
        return script;
    }

    public void LaunchPreparedUpdate(string scriptPath) => Process.Start(new ProcessStartInfo(scriptPath) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden });
}
