using System.Text;

namespace Dlss5Optimizer.SelfTest;

public enum Outcome
{
    Ok,
    Info,
    Warning,
    Failed,
}

/// <summary>Sammelt die Ergebnisse, gibt sie aus und schreibt sie als Markdown (GitHub-Job-Zusammenfassung).</summary>
public sealed class Report
{
    private readonly List<(string Section, Outcome Outcome, string Title, string Detail)> _rows = [];
    private string _section = "";

    public bool Failed => _rows.Any(r => r.Outcome == Outcome.Failed);

    public void Section(string title)
    {
        _section = title;
        Console.WriteLine();
        Console.WriteLine($"== {title} ==");
    }

    public void Add(Outcome outcome, string title, string detail = "")
    {
        _rows.Add((_section, outcome, title, detail));
        Console.WriteLine($"{Symbol(outcome)} {title}{(detail.Length > 0 ? " – " + detail : "")}");
    }

    public void Ok(string title, string detail = "") => Add(Outcome.Ok, title, detail);
    public void Info(string title, string detail = "") => Add(Outcome.Info, title, detail);
    public void Warn(string title, string detail = "") => Add(Outcome.Warning, title, detail);
    public void Fail(string title, string detail = "") => Add(Outcome.Failed, title, detail);

    /// <summary>Prüft eine Bedingung; liefert sie zurück, damit Folgeprüfungen übersprungen werden können.</summary>
    public bool Check(bool condition, string title, string detailIfFailed, string detailIfOk = "")
    {
        if (condition)
            Ok(title, detailIfOk);
        else
            Fail(title, detailIfFailed);
        return condition;
    }

    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        int failed = _rows.Count(r => r.Outcome == Outcome.Failed), warnings = _rows.Count(r => r.Outcome == Outcome.Warning);
        sb.AppendLine($"# DLSS5 Optimizer – Selbsttest ({(OperatingSystem.IsWindows() ? "Windows" : "Linux")})");
        sb.AppendLine();
        sb.AppendLine(failed == 0
            ? $"**Alles bestanden** ({_rows.Count} Prüfungen{(warnings > 0 ? $", {warnings} Hinweise" : "")})."
            : $"**{failed} von {_rows.Count} Prüfungen fehlgeschlagen.**");
        foreach (var group in _rows.GroupBy(r => r.Section))
        {
            sb.AppendLine();
            sb.AppendLine($"## {group.Key}");
            sb.AppendLine();
            sb.AppendLine("| | Prüfung | Ergebnis |");
            sb.AppendLine("|---|---|---|");
            foreach (var r in group)
                sb.AppendLine($"| {Symbol(r.Outcome)} | {Escape(r.Title)} | {Escape(r.Detail)} |");
        }
        return sb.ToString();
    }

    private static string Escape(string s) => s.Replace("|", "\\|").Replace("\r", "").Replace("\n", "<br>");

    private static string Symbol(Outcome o) => o switch
    {
        Outcome.Ok => "✅",
        Outcome.Info => "ℹ️",
        Outcome.Warning => "⚠️",
        _ => "❌",
    };
}
