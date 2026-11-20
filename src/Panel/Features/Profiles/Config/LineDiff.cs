namespace Panel.Features.Profiles.Config;

// Kind: ' ' same, '+' added, '-' removed, '…' a run of unchanged lines collapsed (Text says how many).
public sealed record DiffLine(char Kind, string Text, int? OldLine, int? NewLine);

public sealed record FileDiff(string Name, bool OldExists, DiffLine[] Lines);

// A plain line diff for config files (≤ 64 KB); Monaco's diff view arrives in Phase 4. Empty when nothing changed.
public static class LineDiff
{
    private const long MaxCells = 4_000_000; // ponytail: LCS table cap; above it the changed middle shows as all removed + all added

    public static DiffLine[] Compute(string oldText, string newText, int context = 3)
    {
        var a = Split(oldText);
        var b = Split(newText);
        var pre = 0;
        while (pre < a.Length && pre < b.Length && a[pre] == b[pre]) pre++;
        var suf = 0;
        while (suf < a.Length - pre && suf < b.Length - pre && a[a.Length - 1 - suf] == b[b.Length - 1 - suf]) suf++;
        int n = a.Length - pre - suf, m = b.Length - pre - suf;

        var all = new List<DiffLine>();
        for (var k = 0; k < pre; k++) all.Add(new(' ', a[k], k + 1, k + 1));
        if ((long)n * m <= MaxCells)
        {
            var lcs = new int[n + 1, m + 1];
            for (var i = n - 1; i >= 0; i--)
                for (var j = m - 1; j >= 0; j--)
                    lcs[i, j] = a[pre + i] == b[pre + j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            int x = 0, y = 0;
            while (x < n || y < m)
            {
                if (x < n && y < m && a[pre + x] == b[pre + y]) { all.Add(new(' ', a[pre + x], pre + x + 1, pre + y + 1)); x++; y++; }
                else if (x < n && (y == m || lcs[x + 1, y] >= lcs[x, y + 1])) { all.Add(new('-', a[pre + x], pre + x + 1, null)); x++; }
                else { all.Add(new('+', b[pre + y], null, pre + y + 1)); y++; }
            }
        }
        else
        {
            for (var x = 0; x < n; x++) all.Add(new('-', a[pre + x], pre + x + 1, null));
            for (var y = 0; y < m; y++) all.Add(new('+', b[pre + y], null, pre + y + 1));
        }
        for (var k = 0; k < suf; k++) all.Add(new(' ', a[a.Length - suf + k], a.Length - suf + k + 1, b.Length - suf + k + 1));

        if (all.All(d => d.Kind == ' ')) return [];
        var keep = new bool[all.Count];
        for (var i = 0; i < all.Count; i++)
            if (all[i].Kind != ' ')
                for (var c = Math.Max(0, i - context); c <= Math.Min(all.Count - 1, i + context); c++) keep[c] = true;

        var result = new List<DiffLine>();
        for (var i = 0; i < all.Count;)
        {
            if (keep[i]) { result.Add(all[i++]); continue; }
            var start = i;
            while (i < all.Count && !keep[i]) i++;
            var count = i - start;
            result.Add(new('…', count == 1 ? "1 unchanged line" : $"{count} unchanged lines", null, null));
        }
        return result.ToArray();
    }

    private static string[] Split(string text)
    {
        if (text.Length == 0) return [];
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        if (text.EndsWith('\n')) lines.RemoveAt(lines.Count - 1);
        return lines.ToArray();
    }
}
