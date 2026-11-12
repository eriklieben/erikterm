using System.Diagnostics;

namespace ErikTerm.IntegrationTests.Harness;

/// <summary>Throwaway git repositories for the worktree, merge and review scenarios (like scripts/reference-shots.sh).</summary>
internal sealed class GitRepo(string path)
{
    public string Path { get; } = path;

    public static GitRepo Init(string path)
    {
        Directory.CreateDirectory(path);
        var repo = new GitRepo(path);
        repo.Run("init", "-q", "-b", "main");
        repo.Run("config", "user.name", "integration");
        repo.Run("config", "user.email", "integration@example.invalid");
        repo.Run("config", "commit.gpgsign", "false");
        return repo;
    }

    public GitRepo Write(string file, string content)
    {
        string full = System.IO.Path.Combine(Path, file);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return this;
    }

    public GitRepo Commit(string message)
    {
        Run("add", "-A");
        Run("commit", "-q", "-m", message);
        return this;
    }

    /// <summary>Runs git here; returns stdout. <paramref name="check"/>: fail on a non-zero exit.</summary>
    public string Run(params string[] args) => RunIn(Path, true, args);

    public string TryRun(params string[] args) => RunIn(Path, false, args);

    private static string RunIn(string dir, bool check, string[] args)
    {
        var psi = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add(dir);
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("commit.gpgsign=false");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("user.name=integration");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("user.email=integration@example.invalid");
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        psi.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        psi.Environment["GIT_EDITOR"] = "true";
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (check && p.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)}: {stderr}");
        return stdout.Result;
    }

    /// <summary>A repository with two commits on main.</summary>
    public static GitRepo Simple(string path) =>
        Init(path).Write("README.md", "one\ntwo\nthree\n").Commit("first").Write("src/app.txt", "app\n").Commit("second");

    /// <summary>A repository stopped on a merge with conflicts in two files (scene 15-merge-tool).</summary>
    public static GitRepo MergeConflict(string path)
    {
        var r = Init(path)
            .Write("src/auth.ts", "export const login = (u) => {\n  if (!u) return \"/login\";\n  return \"/home\";\n};\n\nexport const logout = () => \"/login\";\n")
            .Write("src/cart.ts", "// cart helpers\nexport const total = (items) =>\n  items.reduce((a, i) => a + i.price, 0);\n")
            .Write("README.md", "# Shop\n").Commit("base");
        r.Run("switch", "-q", "-c", "feature/signin");
        r.Write("src/auth.ts", "export const login = (u) => {\n  if (!u) return \"/signin\";\n  return \"/home\";\n};\n\nexport const logout = () => \"/signin\";\n")
            .Write("src/cart.ts", "// cart helpers\nexport const total = (items) =>\n  items.reduce((a, i) => a + i.price * i.qty, 0);\n")
            .Write("README.md", "# Shop\n\nSign in.\n").Commit("sign in");
        r.Run("switch", "-q", "main");
        r.Write("src/auth.ts", "export const login = (u) => {\n  if (!u || !u.token) return \"/login\";\n  return \"/home\";\n};\n\nexport const logout = () => \"/login\";\n")
            .Write("src/cart.ts", "// cart helpers\nexport const total = (items) =>\n  items.reduce((a, i) => a + i.price + i.tax, 0);\n")
            .Commit("token and tax");
        r.TryRun("merge", "-q", "--no-edit", "feature/signin");
        return r;
    }

    /// <summary>A repository stopped on a rebase conflict (scene 14-merge-bar).</summary>
    public static GitRepo RebaseConflict(string path)
    {
        var r = Init(path).Write("a.txt", "one\ntwo\nthree\n").Commit("first");
        r.Run("switch", "-q", "-c", "feat");
        r.Write("a.txt", "one\nbranch\nthree\n").Commit("edit on the branch");
        r.Run("switch", "-q", "main");
        r.Write("a.txt", "one\nmain\nthree\n").Commit("edit on main");
        r.Run("switch", "-q", "feat");
        r.TryRun("rebase", "main");
        return r;
    }

    /// <summary>A repository with a worktree on a branch with commits and uncommitted work (scene 18-review).</summary>
    public static (GitRepo Main, string Worktree) WithReviewWorktree(string path)
    {
        var r = Init(path)
            .Write("src/cart.ts", "export const total = (items) =>\n  items.reduce((a, i) => a + i.price, 0);\n")
            .Write("src/auth.ts", "export const logout = () => \"/login\";\n")
            .Write("README.md", "# Shop\n").Commit("base");
        string wt = path + ".worktrees/fix-cart-total";
        r.Run("worktree", "add", "-q", "-b", "fix-cart-total", "--", wt, "main");
        var w = new GitRepo(wt);
        w.Write("src/cart.ts", "export const total = (items) =>\n  items.reduce((a, i) => a + i.price * i.qty, 0);\n").Commit("Fix the cart total");
        w.Write("tests/cart.test.ts", "test(\"total\", () => {});\n").Commit("Add a test");
        w.Write("notes.txt", "notes while reviewing\n");
        r.Write("README.md", "# Shop\n\nmore\n").Commit("later on main");
        return (r, wt);
    }
}
