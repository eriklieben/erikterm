using TUnit.Core.Enums;

namespace ErikTerm.IntegrationTests.Scenarios;

/// <summary>
/// What only Windows or macOS do, off-screen: the system's own shell when default_prog is unset (PowerShell through
/// ConPTY; the login shell through the macOS spawn helper), batch files and cmd in panes (Windows' command-line quoting
/// for cmd), and font lookup without fontconfig (the Windows registry and font folders; the macOS font folders,
/// emoji from Apple Color Emoji). Linux's counterparts (fontconfig, $SHELL) run in every other scenario.
/// </summary>
public class PlatformTests
{
    [Test]
    [RunOn(OS.Windows | OS.MacOs)] // Linux: $SHELL is whatever the machine running the tests has (fish, nushell …), with its own syntax
    public async Task The_system_shell_runs_when_no_program_is_set()
    {
        // Arrange: no default_prog, so Pty.DefaultShell picks: pwsh / powershell on Windows, $SHELL -l on macOS.
        await using var run = AppRun.Start(new Scenario { SystemShell = true, Script = "wait-file:done\nclose-window" });

        // Act
        await run.TypeAsync(OperatingSystem.IsWindows() ? "Write-Output ('shell-' + 6*7)" : "echo shell-$((6*7))");
        string text = await run.WaitForTextAsync("shell-42", timeoutMs: 60_000);

        // Assert: the shell erikterm chose is the pane's program, and it ran the command.
        var panes = await run.PanesAsync();
        string program = panes[0].Str("foreground_process_name");
        if (OperatingSystem.IsWindows()) await Assert.That(program is "pwsh" or "powershell").IsTrue().Because(program);
        else await Assert.That(program).IsNotEmpty();
        await Assert.That(text).Contains("shell-");
        run.Release("done");
        await Checks.CleanExitAsync(run);
    }

    [Test]
    [RunOn(OS.Windows)] // batch files and cmd.exe exist only there
    public async Task Batch_files_and_cmd_run_in_panes_with_their_arguments_intact()
    {
        // Arrange: a .cmd that echoes its arguments (CreateProcess hands it to cmd.exe, so erikterm quotes them for
        // cmd: Pty/WindowsCommandLine.cs, BuildForCmd; unquoted, cmd would split "x=y,z" at = and ,), and cmd.exe found
        // on the system path by its bare name.
        await using var run = AppRun.Start(new Scenario { Script = "wait-file:done\nclose-window" });
        string batch = run.WriteScript("args.cmd", "@echo off\r\necho batch [%~1] [%~2] done\r\npause >nul\r\n");

        // Act
        string first = (await run.CliAsync("split-pane", "--right", "--", batch, "two words", "x=y,z")).Trim();
        string second = (await run.CliAsync("split-pane", "--bottom", "--", "cmd", "/d", "/k", "echo from-cmd-%OS%")).Trim();
        string batchText = await run.WaitForTextAsync("done", ["--pane-id", first], 30_000);
        string cmdText = await run.WaitForTextAsync("from-cmd-Windows_NT", ["--pane-id", second], 30_000);

        // Assert: the arguments arrived as they were given; the foreground program is cmd (the batch file's host).
        await Assert.That(batchText).Contains("batch [two words] [x=y,z] done");
        await Assert.That(cmdText).Contains("from-cmd-Windows_NT");
        var panes = await run.WaitForPanesAsync(p => p.Length == 3);
        await Assert.That(panes.Single(p => p.Int("pane_id") == int.Parse(second)).Str("foreground_process_name")).IsEqualTo("cmd");
        run.Release("done");
        await Checks.CleanExitAsync(run);
    }

    [Test]
    [RunOn(OS.Windows | OS.MacOs)] // Linux looks fonts up with fontconfig, which every scenario already does
    public async Task Fonts_come_from_the_platform_lookup_with_fallbacks()
    {
        // Arrange: two installed families, then one that doesn't exist (a scan of every font file, then the default
        // monospace font); the text has bold and italic faces and characters only fallback fonts have (CJK, symbols,
        // an emoji: on macOS a color bitmap font).
        var (first, second) = OperatingSystem.IsWindows() ? ("Consolas", "Courier New") : ("Menlo", "Courier New");
        await using var run = AppRun.Start(new Scenario
        {
            Config = $"font = \"{first}\"",
            Script = """
                sleep:600
                feed:\e[H\e[2Jplain \e[1mbold\e[0m \e[3mitalic\e[0m \e[1;3mboth\e[0m 漢字 ✓ ★ ∑ 😀\r\n
                sleep:1500
                shot:first.bmp
                wait-file:second
                sleep:2500
                shot:second.bmp
                wait-file:missing
                sleep:2500
                shot:missing.bmp
                wait-file:done
                close-window
                """,
        });
        string config = Path.Combine(run.Directory, "erikterm.toml");

        // Act 1: the second family, through the config watch.
        var one = await Checks.RenderedAsync(run, "first.bmp");
        File.WriteAllText(config, File.ReadAllText(config).Replace($"font = \"{first}\"", $"font = \"{second}\""));
        run.Release("second");

        // Assert 1
        var two = await Checks.RenderedAsync(run, "second.bmp");
        await Checks.DifferAsync(one, two, because: $"{second} instead of {first}");

        // Act 2: a family nobody has.
        File.WriteAllText(config, File.ReadAllText(config).Replace($"font = \"{second}\"", "font = \"No Such Font Mono\""));
        run.Release("missing");

        // Assert 2: a warning, and a frame drawn with the default monospace font.
        await Checks.RenderedAsync(run, "missing.bmp");
        await Wait.UntilAsync(() => run.Output.Contains("no installed font matches \"No Such Font Mono\"", StringComparison.Ordinal), "the font warning");
        run.Release("done");
        await Checks.CleanExitAsync(run);
    }
}
