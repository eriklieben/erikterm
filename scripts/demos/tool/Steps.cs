namespace Demos;

/// <summary>
/// demo steps CLIP.steps FRAMES_DIR: turns a clip's .steps file into an ERIKTERM_DEBUG_SCRIPT_FILE (on stdout). Every
/// line is a debug-script step, plus:
///
///     typeslow:TEXT        types TEXT one character at a time, like a person
///     run:TEXT             typeslow:TEXT, a short pause, Enter
///     end                  stops recording and signals the recorder that the clip is done
///     {FRAMES}             the frames directory (for shot:)
///
/// The steps run every ERIKTERM_SCRIPT_STEP_MS (40 ms); sleep:MS adds a pause after that. A space can't end a step
/// (lines are trimmed), so it goes as typex:\x20 (type: with C# escapes). Lines starting with # are comments.
/// </summary>
internal static class Steps
{
    public static int Run(string[] args)
    {
        string frames = args[1];
        var lines = new List<string>();
        foreach (var raw in File.ReadLines(args[0]))
        {
            string line = raw.TrimEnd('\r').Replace("{FRAMES}", frames, StringComparison.Ordinal), s = line.Trim();
            if (s.Length == 0 || s.StartsWith('#')) continue;
            if (s.StartsWith("typeslow:", StringComparison.Ordinal)) lines.AddRange(TypeSlow(After(line)));
            else if (s.StartsWith("run:", StringComparison.Ordinal))
                lines.AddRange([.. TypeSlow(After(line)), "sleep:250", "key:enter"]);
            else if (s == "end") lines.AddRange(["record:off", "sleep:200", $"shot:{frames}/END.bmp", "sleep:3000"]);
            else lines.Add(s);
        }
        Console.Out.Write(string.Join('\n', lines) + "\n");
        return 0;
    }

    private static string After(string line) => line[(line.IndexOf(':') + 1)..];

    private static IEnumerable<string> TypeSlow(string text) =>
        text.Select(c => c switch { ' ' => "typex:\\x20", '\\' => "typex:\\\\", _ => "type:" + c });
}
