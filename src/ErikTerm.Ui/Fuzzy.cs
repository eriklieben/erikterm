namespace ErikTerm.Ui;

/// <summary>
/// fzf-style fuzzy matching: the pattern's characters must appear in order (case-insensitive).
/// The best-scoring alignment is found with dynamic programming, favouring matches at word starts
/// and consecutive runs, so "spr" ranks "Split Right" above "Scroll Page Right…"-like noise.
/// </summary>
public static class Fuzzy
{
    private const int MatchScore = 16;
    private const int WordStartBonus = 24;
    private const int FirstCharBonus = 8;
    private const int ConsecutiveBonus = 20;
    private const int GapPenalty = 3;
    private const int Unreachable = int.MinValue / 4;

    /// <summary>Returns true when every pattern char matches; <paramref name="positions"/> gets the matched indices.</summary>
    public static bool Match(string pattern, string text, out int score, List<int>? positions = null)
    {
        score = 0;
        positions?.Clear();
        pattern = pattern.Trim();
        if (pattern.Length == 0) return true;
        int m = pattern.Length, n = text.Length;
        if (m > n) return false;
        if (!IsSubsequence(pattern, text)) return false;

        // dp[i, j]: best score with pattern[i] matched at text[j].
        var dp = new int[m, n];
        var from = new int[m, n]; // index of the previous match (for backtracking), -1 for i == 0
        for (int i = 0; i < m; i++) FillRow(pattern, text, i, dp, from);

        int bestJ = -1, best = Unreachable;
        for (int j = 0; j < n; j++)
            if (dp[m - 1, j] > best) { best = dp[m - 1, j]; bestJ = j; }
        if (bestJ < 0) return false;

        score = best;
        if (positions is not null)
        {
            for (int i = m - 1, j = bestJ; i >= 0; j = from[i, j], i--) positions.Add(j);
            positions.Reverse();
        }
        return true;
    }

    private static bool IsSubsequence(string pattern, string text)
    {
        int i = 0;
        for (int j = 0; j < text.Length && i < pattern.Length; j++)
            if (Eq(pattern[i], text[j])) i++;
        return i == pattern.Length;
    }

    private static void FillRow(string pattern, string text, int i, int[,] dp, int[,] from)
    {
        // Gap transition from an earlier match k (k <= j-2) costs GapPenalty*(j-1-k), so track
        // max(dp[i-1,k] + GapPenalty*k) as j advances and subtract GapPenalty*(j-1) at use.
        int runBest = Unreachable, runIndex = -1;
        for (int j = 0; j < text.Length; j++)
        {
            if (i > 0 && j >= 2 && dp[i - 1, j - 2] > Unreachable)
            {
                int v = dp[i - 1, j - 2] + GapPenalty * (j - 2);
                if (v > runBest) { runBest = v; runIndex = j - 2; }
            }

            if (j < i || !Eq(pattern[i], text[j])) { dp[i, j] = Unreachable; continue; }

            int bonus = MatchScore + Bonus(text, j);
            if (i == 0) ScoreFirstChar(dp, from, j, bonus);
            else ScoreAfterPrevious(dp, from, i, j, bonus, runBest, runIndex);
        }
    }

    private static void ScoreFirstChar(int[,] dp, int[,] from, int j, int bonus)
    {
        dp[0, j] = bonus - Math.Min(j, 15) + (j == 0 ? FirstCharBonus : 0);
        from[0, j] = -1;
    }

    /// <summary>Scores pattern[i] at text[j] from the better of the adjacent match and the best gapped one.</summary>
    private static void ScoreAfterPrevious(int[,] dp, int[,] from, int i, int j, int bonus, int runBest, int runIndex)
    {
        int consecutive = dp[i - 1, j - 1] > Unreachable ? dp[i - 1, j - 1] + ConsecutiveBonus : Unreachable;
        int gapped = runBest > Unreachable ? runBest - GapPenalty * (j - 1) : Unreachable;
        if (consecutive == Unreachable && gapped == Unreachable) { dp[i, j] = Unreachable; return; }
        if (consecutive >= gapped) { dp[i, j] = consecutive + bonus; from[i, j] = j - 1; }
        else { dp[i, j] = gapped + bonus; from[i, j] = runIndex; }
    }

    private static bool Eq(char a, char b) => char.ToLowerInvariant(a) == char.ToLowerInvariant(b);

    private static int Bonus(string text, int j)
    {
        if (j == 0) return WordStartBonus;
        char prev = text[j - 1], c = text[j];
        if (prev is ' ' or '-' or '_' or '/' or ':' or '.' or '(' or '[') return WordStartBonus;
        if (char.IsLower(prev) && char.IsUpper(c)) return WordStartBonus - 4; // camelCase
        return 0;
    }
}
