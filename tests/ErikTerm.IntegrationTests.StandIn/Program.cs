using System.Diagnostics;

// NAME.exe runs the sh script NAME next to it with its arguments, under the sh named by FAKE_SH, and exits with its
// status. Standard input, output and error are this process's own (the pane's console, or the pipes ToolRunner made).
string self = Environment.ProcessPath ?? throw new InvalidOperationException("no process path");
string script = Path.Combine(Path.GetDirectoryName(self)!, Path.GetFileNameWithoutExtension(self)).Replace('\\', '/');
string sh = Environment.GetEnvironmentVariable("FAKE_SH") is { Length: > 0 } s ? s : "sh";
if (!File.Exists(script))
{
    Console.Error.WriteLine($"standin: no script {script}");
    return 127;
}
var start = new ProcessStartInfo(sh) { UseShellExecute = false };
start.ArgumentList.Add(script);
foreach (string a in args) start.ArgumentList.Add(a);
using var p = Process.Start(start)!;
p.WaitForExit();
return p.ExitCode;
