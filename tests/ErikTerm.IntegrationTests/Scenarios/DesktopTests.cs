using System.Diagnostics;
using System.Text.Json;
using TUnit.Core.Enums;

namespace ErikTerm.IntegrationTests.Scenarios;

/// <summary>
/// Scenarios with a real window ([Desktop]: CI's Windows and macOS runners only). What needs one: the offscreen video
/// driver has no HWND or NSWindow, so on Windows the UI Automation provider (Accessibility/), the Win32 hit test and
/// caption double click (Chrome/WindowHitTest.cs, CaptionHook.cs) and the DWM backdrop (Native/WindowsDwm.cs) are
/// never reached off-screen; on macOS the Cocoa window, its menu and the borderless chrome.
/// </summary>
public class DesktopTests
{
    // Win32 hit-test codes (WinUser.h).
    private const int HTCLIENT = 1, HTCAPTION = 2, HTLEFT = 10, HTTOPLEFT = 13;

    [Test]
    [Desktop]
    [RunOn(OS.Windows)] // UI Automation, WM_NCHITTEST and DWM are Windows'
    public async Task Windows_screen_readers_see_the_panes_and_the_borderless_window_moves_and_maximizes()
    {
        // Arrange: a borderless translucent window with acrylic (the window draws its own chrome and asks DWM for the
        // blur), one pane with a marker, then a split.
        await using var run = AppRun.Start(new Scenario
        {
            Desktop = true,
            Config = "window_decorations = \"NONE\"\nwindow_background_opacity = 0.85\nwindow_blur = \"acrylic\"",
            Script = """
                wait-file:typed
                sleep:300
                shot:desktop.bmp
                action:SplitHorizontal
                sleep:600
                wait-file:uia
                sleep:400
                shot:selected.bmp
                wait-file:done
                close-window
                """,
        });
        await run.TypeAsync("echo uia-marker-$((6*7))");
        await run.WaitForTextAsync("uia-marker-42");
        run.Release("typed");
        await Checks.RenderedAsync(run, "desktop.bmp");
        var panes = await run.WaitForPanesAsync(p => p.Length == 2);
        nint hwnd = await WindowOfAsync(run);

        // Act 1: a UI Automation client (Windows PowerShell's) reads the window's tree.
        var tree = await UiaAsync(run, hwnd);

        // Assert 1: the window is erikterm's, each pane an element with the text pattern, the marker in one of them.
        await Assert.That(tree.GetProperty("framework").GetString()).IsEqualTo("erikterm").Because(tree.ToString());
        var elements = tree.GetProperty("panes").EnumerateArray().ToArray();
        await Assert.That(elements.Length).IsEqualTo(2).Because(tree.ToString());
        await Assert.That(elements.All(e => e.GetProperty("type").GetString() == "terminal")).IsTrue();
        var marked = elements.Single(e => (e.GetProperty("text").GetString() ?? "").Contains("uia-marker-42", StringComparison.Ordinal));
        await Assert.That(marked.GetProperty("found").GetBoolean()).IsTrue().Because(marked.ToString());

        // Act 2: focus that pane and select the marker through UI Automation.
        string id = marked.GetProperty("id").GetString()!;
        await UiaAsync(run, hwnd, id);

        // Assert 2: erikterm carried out both (the pane with the marker is the active one now).
        int targetPane = int.Parse(id["pane-".Length..]);
        await run.WaitForPanesAsync(p => p.Single(x => x.Bool("is_active")).Int("pane_id") == targetPane);
        run.Release("uia");
        await Checks.RenderedAsync(run, "selected.bmp");

        // Act / Assert 3: Windows asks the window what is under the pointer; the borderless window answers itself.
        await Assert.That(Platform.GetWindowRect(hwnd, out var r)).IsTrue();
        await Assert.That(Platform.HitTest(hwnd, r.Left + 1, r.Top + 1)).IsEqualTo(HTTOPLEFT);
        await Assert.That(Platform.HitTest(hwnd, r.Left + 1, (r.Top + r.Bottom) / 2)).IsEqualTo(HTLEFT);
        await Assert.That(Platform.HitTest(hwnd, (r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2)).IsEqualTo(HTCLIENT);
        // The tab bar's empty strip (right of the one tab, left of the caption buttons) drags the window.
        var strip = Enumerable.Range(0, 6).Select(i => Platform.HitTest(hwnd, (r.Left + r.Right) / 2, r.Top + 8 + i * 5)).ToArray();
        await Assert.That(strip).Contains(HTCAPTION).Because(string.Join(",", strip));

        // Act / Assert 4: a double click on that strip maximizes the window (CaptionHook sees the posted message).
        int y = r.Top + 8 + Array.IndexOf(strip, HTCAPTION) * 5;
        Platform.PostMessage(hwnd, 0x00A3 /* WM_NCLBUTTONDBLCLK */, HTCAPTION, (nint)((y << 16) | (((r.Left + r.Right) / 2) & 0xFFFF)));
        await Wait.UntilAsync(() => Platform.GetWindowRect(hwnd, out var now) && (now.Right - now.Left) != (r.Right - r.Left), "the window maximized", 10_000);
        run.Release("done");
        await Checks.CleanExitAsync(run);
    }

    [Test]
    [Desktop]
    [RunOn(OS.MacOs)] // the Cocoa window
    public async Task A_mac_window_draws_splits_and_borderless_chrome_and_closes()
    {
        // Arrange: a real Cocoa window, borderless (erikterm's own chrome, macOS keeps its buttons on the left).
        await using var run = AppRun.Start(new Scenario
        {
            Desktop = true,
            Config = "window_decorations = \"NONE\"\nwindow_background_opacity = 0.9",
            Script = """
                sleep:1200
                shot:window.bmp
                action:SplitHorizontal
                sleep:600
                move:500,3
                sleep:400
                hovertab:0
                shot:split.bmp
                action:ToggleFullScreen
                sleep:1200
                action:ToggleFullScreen
                sleep:1200
                wait-file:done
                close-window
                """,
        });

        // Act
        var window = await Checks.RenderedAsync(run, "window.bmp");
        var split = await Checks.RenderedAsync(run, "split.bmp");

        // Assert: a split of an empty pane adds a divider, a prompt and a cursor, about 0.2 % of a 1016×718 frame
        // (CI's macOS window is at 1x; 0.19 % changed there).
        await Checks.DifferAsync(window, split, 0.001, "the split");
        await run.WaitForPanesAsync(p => p.Length == 2);
        run.Release("done");
        await Checks.CleanExitAsync(run);
    }

    /// <summary>The run's top-level window (Windows), once it is visible.</summary>
    private static async Task<nint> WindowOfAsync(AppRun run)
    {
        nint hwnd = 0;
        await Wait.UntilAsync(() => (hwnd = Platform.WindowsOf(run.ProcessId).FirstOrDefault()) != 0, "a visible window", 20_000);
        return hwnd;
    }

    /// <summary>
    /// Asks UI Automation about the window from another process, as a screen reader would: Windows PowerShell 5.1 with
    /// .NET Framework's UI Automation client (System.Windows.Automation). With <paramref name="focus"/> (an element's
    /// AutomationId) it focuses that pane and selects the marker in it instead. The script's JSON.
    /// </summary>
    private static async Task<JsonElement> UiaAsync(AppRun run, nint hwnd, string? focus = null)
    {
        string script = run.WriteScript("uia.ps1", UiaScript);
        var start = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-Hwnd", ((long)hwnd).ToString() })
            start.ArgumentList.Add(a);
        if (focus is not null)
        {
            start.ArgumentList.Add("-Focus");
            start.ArgumentList.Add(focus);
        }
        using var p = Process.Start(start)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(60_000);
        await p.WaitForExitAsync(cts.Token);
        string output = await stdout;
        if (p.ExitCode != 0) throw new InvalidOperationException($"uia.ps1 exited {p.ExitCode}:\n{output}\n{await stderr}\nlog:\n{run.Output}");
        using var doc = JsonDocument.Parse(output);
        return doc.RootElement.Clone();
    }

    private const string UiaScript = """
        param([long]$Hwnd, [string]$Focus = '')
        $ErrorActionPreference = 'Stop'
        Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, WindowsBase
        # The optional parts report a failure as text instead of stopping the script.
        function Attempt([scriptblock]$Block) { try { & $Block } catch { 'error: ' + $_.Exception.Message } }
        $A = [System.Windows.Automation.AutomationElement]
        $Unit = [System.Windows.Automation.Text.TextUnit]
        $root = $A::FromHandle([IntPtr]$Hwnd)
        $cond = New-Object System.Windows.Automation.PropertyCondition($A::ClassNameProperty, 'ErikTermPane')
        $panes = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
        if ($Focus -ne '') {
            foreach ($p in $panes) {
                if ($p.Current.AutomationId -ne $Focus) { continue }
                $p.SetFocus()
                $tp = $p.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern)
                $hit = $tp.DocumentRange.FindText('uia-marker-42', $true, $false)
                if ($hit -ne $null) { $hit.Select(); $hit.ScrollIntoView($true) }
            }
            Start-Sleep -Milliseconds 500
            '{"done":true}'
            exit 0
        }
        $out = [ordered]@{ name = $root.Current.Name; framework = $root.Current.FrameworkId; panes = @() }
        foreach ($p in $panes) {
            $tp = $p.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern)
            $doc = $tp.DocumentRange
            $line = $doc.Clone()
            $line.ExpandToEnclosingUnit($Unit::Line)
            $Start = [System.Windows.Automation.Text.TextPatternRangeEndpoint]::Start
            $found = $doc.FindText('uia-marker-42', $false, $true)
            $out.panes += [ordered]@{
                name = $p.Current.Name
                id = $p.Current.AutomationId
                type = $p.Current.LocalizedControlType
                focused = $p.Current.HasKeyboardFocus
                text = $doc.GetText(-1)
                found = ($found -ne $null)
                line = Attempt { $line.GetText(200) }
                word = Attempt { $w = $doc.Clone(); $null = $w.Move($Unit::Word, 2); $w.ExpandToEnclosingUnit($Unit::Word); $null = $w.MoveEndpointByUnit([System.Windows.Automation.Text.TextPatternRangeEndpoint]::End, $Unit::Character, 3); $w.GetText(50) }
                rectangles = Attempt { @($line.GetBoundingRectangles()).Count }
                visible = Attempt { @($tp.GetVisibleRanges()).Count }
                selection = Attempt { @($tp.GetSelection()).Count }
                compare = Attempt { $doc.CompareEndpoints($Start, $line, $Start) }
                atPoint = Attempt { $b = $p.Current.BoundingRectangle; $tp.RangeFromPoint((New-Object System.Windows.Point(($b.X + 20), ($b.Y + 10)))).GetText(20) }
                enclosing = Attempt { $line.GetEnclosingElement().Current.AutomationId }
            }
        }
        $out | ConvertTo-Json -Depth 4 -Compress
        """;
}
