using System.Diagnostics;

namespace AbsolverModTool.Core;

public static class UnrealPak
{
    public record Result(int ExitCode, string Output);

    public static Result Run(string arguments)
    {
        var psi = new ProcessStartInfo(Config.UnrealPakPath, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        return new Result(proc.ExitCode, stdout + stderr);
    }
}
