using System.Diagnostics;
using Stringline.Gateway.Tests;

namespace Stringline.App.Tests;

/// <summary>
/// <c>build/sign.ps1</c> (#1), which publish.ps1 and the installer build call: without a certificate it leaves the
/// files unsigned and succeeds, and it never prints the certificate's password.
/// </summary>
public sealed class SigningScriptTests : IDisposable
{
    private const string Password = "not-a-real-password-7731";

    private static readonly string Script = Path.GetFullPath(Fixtures.Path(Path.Combine("..", "..", "build", "sign.ps1")));

    private readonly string _file = Path.Combine(Path.GetTempPath(), $"stringline-sign-{Guid.NewGuid():N}.exe");

    public SigningScriptTests() => File.WriteAllBytes(_file, [0x4D, 0x5A, 1, 2, 3]);

    public void Dispose() => File.Delete(_file);

    [Fact]
    public async Task Skips_signing_with_a_warning_when_there_is_no_certificate()
    {
        var (exit, output) = await RunAsync(pfx: null);

        exit.Should().Be(0, output);
        output.Should().Contain("Not signing").And.Contain("no code-signing certificate");
        File.ReadAllBytes(_file).Should().Equal(0x4D, 0x5A, 1, 2, 3);
    }

    [Fact]
    public async Task Fails_on_a_missing_certificate_without_printing_its_password()
    {
        var (exit, output) = await RunAsync(pfx: Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.pfx"));

        exit.Should().NotBe(0);
        output.Should().Contain("does not exist").And.NotContain(Password);
    }

    private async Task<(int Exit, string Output)> RunAsync(string? pfx)
    {
        var start = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Script, _file })
        {
            start.ArgumentList.Add(arg);
        }

        start.Environment.Remove("STRINGLINE_SIGN_PFX");
        if (pfx is not null)
        {
            start.Environment["STRINGLINE_SIGN_PFX"] = pfx;
        }

        start.Environment["STRINGLINE_SIGN_PASSWORD"] = Password;
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        // PowerShell wraps redirected warnings and errors at the console width.
        return (process.ExitCode, System.Text.RegularExpressions.Regex.Replace(await stdout + await stderr, @"\s+", " "));
    }
}
