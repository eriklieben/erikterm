using System.Diagnostics;
using System.Reflection;

namespace Demos;

/// <summary>
/// demo world WORLD_DIR ERIKTERM_BIN_DIR: the throwaway demo world for the README clips. A HOME with a clean bash
/// prompt, a small git repository (tidewatch, a tide forecast CLI in C#), the fake coding agent (helper), a stand-in
/// dotnet with steady build and test output, and a few colour schemes. Nothing personal. Prints the HOME.
/// </summary>
internal static class World
{
    private const string Repo = "tidewatch/";

    public static int Run(string[] args)
    {
        string world = Path.GetFullPath(args[0]), bindir = Path.GetFullPath(args[1]);
        string home = Path.Combine(world, "home");
        if (Directory.Exists(world)) Directory.Delete(world, recursive: true);
        Directory.CreateDirectory(home);
        var w = new Writer(home);

        Shell(w, bindir);
        Neovim(w);
        Schemes(w);
        Tools(w, home);
        Project(w);
        History(w, home);
        Console.WriteLine(home);
        return 0;
    }

    // ---- shell ----------------------------------------------------------------------------------------------------
    private const string Branch = ""; // the Nerd Font git branch glyph

    private static void Shell(Writer w, string bindir)
    {
        w.Write(".bashrc", $$"""
            # demo shell: short prompt, no user or host anywhere
            PROMPT_COMMAND=()
            export PATH="$HOME/.local/bin:{{bindir}}:/usr/local/bin:/usr/bin:/bin"
            export EDITOR=nvim LESS=-R PAGER=less GIT_PAGER=cat
            export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
            HISTFILE=$HOME/.bash_history
            # segments: the directory, the git branch, and the .NET SDK when the repository has a global.json
            __demo_prompt() {
                local top branch
                { read -r top; read -r branch; } < <(git rev-parse --show-toplevel --abbrev-ref HEAD 2>/dev/null)
                PS1='\[\e[34m\]\w\[\e[0m\]'
                [[ -n $branch ]] && PS1+=" \[\e[32m\]{{Branch}} $branch\[\e[0m\]"
                [[ -n $top && -e $top/global.json ]] && PS1+=' \[\e[35m\].NET 10.0\[\e[0m\]'
                PS1+=' \[\e[1m\]$\[\e[0m\] '
            }
            PROMPT_COMMAND=(__demo_prompt 'printf "\e]0;%s\a" "${PWD/#$HOME/\~}"')
            alias ls='ls --color=auto' ll='ls -l --color=auto'
            cd "$HOME/tidewatch" 2>/dev/null

            """);
        w.Write(".bash_profile", "[[ -f ~/.bashrc ]] && . ~/.bashrc\n");
        w.Write(".inputrc", "set bell-style none\n");
        w.Write(".gitconfig", """
            [user]
                name = demo
                email = demo@example.com
            [init]
                defaultBranch = main
            [commit]
                gpgsign = false
            [advice]
                detachedHead = false
            [color]
                ui = always

            """);
    }

    // ---- neovim: a clean look, no plugins -------------------------------------------------------------------------
    private static void Neovim(Writer w) => w.Write(".config/nvim/init.lua", """
        -- the terminal's 16 colors, so a theme change in erikterm recolors Neovim too
        vim.o.termguicolors = false
        vim.o.number = true
        vim.o.relativenumber = true
        vim.o.signcolumn = "no"
        vim.o.laststatus = 2
        vim.o.showmode = false
        vim.o.ruler = false
        vim.o.shortmess = vim.o.shortmess .. "IF"
        vim.o.swapfile = false
        vim.o.shada = ""
        vim.o.cursorline = true
        vim.o.cursorlineopt = "number"
        vim.o.wrap = false
        vim.opt.fillchars = { eob = " " }
        vim.cmd("highlight clear")
        local hl = function(g, o) vim.api.nvim_set_hl(0, g, o) end
        hl("Normal", {})
        hl("Comment", { ctermfg = 8, italic = true })
        hl("String", { ctermfg = 2 })
        hl("Character", { ctermfg = 2 })
        hl("Constant", { ctermfg = 3 })
        hl("Number", { ctermfg = 3 })
        hl("Float", { ctermfg = 3 })
        hl("Boolean", { ctermfg = 3 })
        hl("Statement", { ctermfg = 5 })
        hl("Keyword", { ctermfg = 5 })
        hl("Conditional", { ctermfg = 5 })
        hl("Repeat", { ctermfg = 5 })
        hl("Include", { ctermfg = 5 })
        hl("PreProc", { ctermfg = 5 })
        hl("StorageClass", { ctermfg = 5 })
        hl("Structure", { ctermfg = 5 })
        hl("Function", { ctermfg = 4 })
        hl("Type", { ctermfg = 6 })
        hl("Identifier", {})
        hl("Special", { ctermfg = 12 })
        hl("SpecialComment", { ctermfg = 8, italic = true })
        hl("Operator", { ctermfg = 6 })
        hl("Delimiter", { ctermfg = 8 })
        hl("LineNr", { ctermfg = 8 })
        hl("CursorLineNr", { ctermfg = 3, bold = true })
        hl("StatusLine", { ctermfg = 0, ctermbg = 4 })
        hl("StatusLineNC", { ctermfg = 8 })
        hl("MsgArea", {})
        vim.o.statusline = " %f %m%= %l:%c  %{&filetype} "

        """);

    // ---- a few extra color schemes for the palette clip (well-known palettes) -------------------------------------
    private static void Schemes(Writer w)
    {
        (string Name, string Fg, string Bg, string Sel, string[] Ansi, string[] Brights)[] schemes =
        [
            ("Gruvbox Dark", "#ebdbb2", "#282828", "#504945",
                ["#282828", "#cc241d", "#98971a", "#d79921", "#458588", "#b16286", "#689d6a", "#a89984"],
                ["#928374", "#fb4934", "#b8bb26", "#fabd2f", "#83a598", "#d3869b", "#8ec07c", "#ebdbb2"]),
            ("Dracula", "#f8f8f2", "#282a36", "#44475a",
                ["#21222c", "#ff5555", "#50fa7b", "#f1fa8c", "#bd93f9", "#ff79c6", "#8be9fd", "#f8f8f2"],
                ["#6272a4", "#ff6e6e", "#69ff94", "#ffffa5", "#d6acff", "#ff92df", "#a4ffff", "#ffffff"]),
            ("Nord", "#d8dee9", "#2e3440", "#434c5e",
                ["#3b4252", "#bf616a", "#a3be8c", "#ebcb8b", "#81a1c1", "#b48ead", "#88c0d0", "#e5e9f0"],
                ["#4c566a", "#bf616a", "#a3be8c", "#ebcb8b", "#81a1c1", "#b48ead", "#8fbcbb", "#eceff4"]),
            ("Rose Pine Dawn", "#575279", "#faf4ed", "#dfdad9",
                ["#f2e9e1", "#b4637a", "#286983", "#ea9d34", "#56949f", "#907aa9", "#d7827e", "#575279"],
                ["#9893a5", "#b4637a", "#286983", "#ea9d34", "#56949f", "#907aa9", "#d7827e", "#575279"]),
        ];
        static string List(string[] xs) => "[" + string.Join(", ", xs.Select(x => $"\"{x}\"")) + "]";
        foreach (var s in schemes)
            w.Write($".config/erikterm/colors/{s.Name.ToLowerInvariant().Replace(' ', '-')}.toml",
                $"[colors]\nforeground = \"{s.Fg}\"\nbackground = \"{s.Bg}\"\ncursor_bg = \"{s.Fg}\"\n" +
                $"selection_bg = \"{s.Sel}\"\nansi = {List(s.Ansi)}\nbrights = {List(s.Brights)}\n\n" +
                $"[metadata]\nname = \"{s.Name}\"\n");
    }

    // ---- this program as helper (the fake agent) and dotnet (the stand-in) -----------------------------------------
    private static void Tools(Writer w, string home)
    {
        string lib = Path.Combine(home, ".local/lib/demo");
        Directory.CreateDirectory(lib);
        foreach (var f in Directory.GetFiles(AppContext.BaseDirectory))
            if (Path.GetFileName(f).StartsWith("demo", StringComparison.Ordinal))
                File.Copy(f, Path.Combine(lib, Path.GetFileName(f)));
        File.SetUnixFileMode(Path.Combine(lib, "demo"), (UnixFileMode)0b111_101_101);
        // links, not scripts: the kernel names the process after the link from its very first moment (a terminal that
        // reads /proc/PID/comm right away sees "dotnet", never "demo"), and demo picks its command from argv[0]
        string bin = Path.Combine(home, ".local/bin");
        Directory.CreateDirectory(bin);
        foreach (var name in new[] { "helper", "dotnet" })
            File.CreateSymbolicLink(Path.Combine(bin, name), "../lib/demo/demo");
    }

    // ---- the project ----------------------------------------------------------------------------------------------
    private static void Project(Writer w)
    {
        w.Write(Repo + "README.md", """
            # tidewatch

            Tide forecasts in your terminal. Reads harmonic constituents from NOAA-style station files
            and prints the next highs and lows, with a chart of the next two days.

            ```sh
            dotnet run --project src/Tidewatch -- forecast stations/9414290.txt --days 3
            dotnet run --project src/Tidewatch -- chart
            ```

            See https://github.com/example/tidewatch for the station format.

            """);
        w.Write(Repo + ".gitignore", "bin/\nobj/\nTestResults/\n");
        w.Write(Repo + "global.json", """
            {
              "test": {
                "runner": "Microsoft.Testing.Platform"
              }
            }

            """);
        w.Write(Repo + "Tidewatch.slnx", """
            <Solution>
              <Folder Name="/src/">
                <Project Path="src/Tidewatch/Tidewatch.csproj" />
              </Folder>
              <Folder Name="/tests/">
                <Project Path="tests/Tidewatch.Tests/Tidewatch.Tests.csproj" />
              </Folder>
            </Solution>

            """);
        w.Write(Repo + "src/Tidewatch/Tidewatch.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">

              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                <AssemblyName>tidewatch</AssemblyName>
                <Version>0.4.0</Version>
              </PropertyGroup>

            </Project>

            """);
        w.Write(Repo + "src/Tidewatch/Program.cs", """
            using Tidewatch;

            string command = args.Length > 0 ? args[0] : "forecast";
            string stationFile = args.Skip(1).FirstOrDefault(a => a.EndsWith(".txt")) ?? "stations/9414290.txt";
            int days = Option("--days") is { } d ? int.Parse(d) : 1;
            string unit = Option("--unit") ?? "m";
            var station = Station.Parse(File.ReadAllText(stationFile));

            switch (command)
            {
                case "forecast":
                    Commands.PrintForecast(Console.Out, station, DateTime.Now, days, unit);
                    return 0;
                case "chart":
                    Commands.PrintChart(Console.Out, station, DateTime.Today, hours: 48);
                    return 0;
                default:
                    Console.Error.WriteLine("usage: tidewatch [forecast|chart] [STATION_FILE] [--days N] [--unit m|ft]");
                    return 2;
            }

            string? Option(string name)
            {
                int i = Array.IndexOf(args, name);
                return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
            }

            """);
        foreach (var name in new[] { "Units", "Station", "Forecast", "Commands", "Chart" })
            w.Write(Repo + $"src/Tidewatch/{name}.cs", Source(name));
        w.Write(Repo + "stations/9414290.txt", """
            # San Francisco, harmonic constituents (sample data)
            id=9414290;name=San Francisco;lat=37.8063;lon=-122.4659;datum=0.97
            M2 0.580 330.0 28.9841042
            S2 0.135 333.0 30.0000000
            N2 0.122 306.0 28.4397295
            K1 0.368 106.0 15.0410686
            O1 0.229 89.0 13.9430356

            """);
        w.Write(Repo + "tests/Tidewatch.Tests/Tidewatch.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">

              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>

              <ItemGroup>
                <PackageReference Include="TUnit" Version="1.76.0" />
              </ItemGroup>

              <ItemGroup>
                <ProjectReference Include="../../src/Tidewatch/Tidewatch.csproj" />
              </ItemGroup>

            </Project>

            """);
        foreach (var (file, text) in WorldTests.Committed)
            w.Write(Repo + "tests/Tidewatch.Tests/" + file, text);
    }

    // ---- history: main, then a feature branch with moon phases and some uncommitted work ------------------------------
    private static void History(Writer w, string home)
    {
        string repo = Path.Combine(home, "tidewatch");
        var git = new Git(repo, home);
        git.Run(null, "init", "-q", "-b", "main");
        git.Commit("Parse station files and print highs and lows", "2026-10-02T10:12:00");
        string[] messages =
        [
            "Add feet as a unit",
            "Skip comment lines in station files",
            "Find extremes with a 6-minute step",
            "Add the San Francisco sample station",
            "Print the station name above the table",
            "Test the CLI flags",
        ];
        for (int i = 0; i < messages.Length; i++)
        {
            File.AppendAllText(Path.Combine(repo, "CHANGELOG.md"), $"- {messages[i]}\n");
            git.Commit(messages[i], $"2026-10-{5 + i * 3:00}T1{i}:2{i}:00");
        }

        git.Run(null, "switch", "-q", "-c", "feature/moon-phase");
        w.Write(Repo + "src/Tidewatch/Moon.cs", Source("Moon"));
        git.Commit("Add moon phases", "2026-11-03T09:40:00");
        w.Edit(Repo + "src/Tidewatch/Commands.cs",
            "        output.WriteLine($\"{station.Name} ({station.Id})\");\n",
            "        output.WriteLine($\"{station.Name} ({station.Id})  ·  {Moon.PhaseName(start)}\");\n" +
            "        if (Moon.IsSpringTide(start))\n" +
            "            output.WriteLine(\"  spring tide: expect bigger ranges\");\n");
        w.Edit(Repo + "src/Tidewatch/Units.cs", "        _ => $\"{metres:0.00} m\",\n",
            "        _ => $\"{metres:0.00} m\", // two decimals, like the station files\n");
        git.Commit("Show the moon phase and spring tides in the forecast", "2026-11-04T15:05:00");

        // uncommitted work on the branch: a test in progress and a finer step
        w.Write(Repo + "tests/Tidewatch.Tests/MoonTests.cs", WorldTests.Moon);
        w.Edit(Repo + "src/Tidewatch/Forecast.cs", "int stepMinutes = 6", "int stepMinutes = 5");
    }

    /// <summary>
    /// A long, believable request log for scrollback search (the stand-in dotnet run -- serve prints it); lines fit half
    /// a window.
    /// </summary>
    internal static string ServerLog()
    {
        var rng = new Random(7);
        string[] paths = ["/api/forecast", "/api/stations", "/api/stations/9414290", "/healthz", "/api/moon", "/js/site.js"];
        (int Status, int Weight)[] statuses = [(200, 60), (200, 20), (200, 10), (304, 5), (404, 3), (500, 1)];
        int total = statuses.Sum(s => s.Weight);
        var log = new System.Text.StringBuilder();
        for (int i = 0; i < 900; i++)
        {
            string t = $"09:{i / 60 % 60:00}:{i % 60:00}.{rng.Next(100, 1000)}";
            string path = paths[rng.Next(paths.Length)];
            int pick = rng.Next(total), status = 200;
            foreach (var s in statuses)
            {
                if (pick < s.Weight) { status = s.Status; break; }
                pick -= s.Weight;
            }
            string level = status < 400 ? "info" : status < 500 ? "warn" : "fail";
            log.Append($"{t} {level} GET {path} {status} {rng.Next(2, 141)} ms\n");
            if (i is 311 or 642) log.Append($"{t} fail Upstream timeout for station 9414290 (3/3)\n");
            if (i is 150 or 480 or 820) log.Append($"{t} info Computed extremes for 9414290 in {rng.Next(8, 31)} ms\n");
        }
        return log.ToString();
    }

    private static string Source(string name)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream($"Tidewatch/{name}.cs")
            ?? throw new InvalidOperationException($"no embedded Tidewatch/{name}.cs");
        return new StreamReader(s).ReadToEnd();
    }

    private sealed class Writer(string home)
    {
        public void Write(string path, string text, bool executable = false)
        {
            string full = Path.Combine(home, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
            File.SetUnixFileMode(full, executable ? (UnixFileMode)0b111_101_101 : (UnixFileMode)0b110_100_100);
        }

        public void Edit(string path, string from, string to)
        {
            string full = Path.Combine(home, path), text = File.ReadAllText(full);
            if (!text.Contains(from, StringComparison.Ordinal)) throw new InvalidOperationException($"{path}: no '{from.Trim()}'");
            File.WriteAllText(full, text.Replace(from, to, StringComparison.Ordinal));
        }
    }

    private sealed class Git(string repo, string home)
    {
        public void Commit(string message, string date)
        {
            Run(null, "add", "-A");
            Run(date, "commit", "-q", "-m", message);
        }

        public void Run(string? date, params string[] args)
        {
            var psi = new ProcessStartInfo("git") { WorkingDirectory = repo, RedirectStandardOutput = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.Environment["HOME"] = home;
            psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
            psi.Environment["GIT_AUTHOR_NAME"] = psi.Environment["GIT_COMMITTER_NAME"] = "demo";
            psi.Environment["GIT_AUTHOR_EMAIL"] = psi.Environment["GIT_COMMITTER_EMAIL"] = "demo@example.com";
            psi.Environment["TZ"] = "UTC"; // the dates carry no zone of the machine that builds the world
            if (date is not null) psi.Environment["GIT_AUTHOR_DATE"] = psi.Environment["GIT_COMMITTER_DATE"] = date + "+00:00";
            using var p = Process.Start(psi)!;
            p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)} failed");
        }
    }
}
