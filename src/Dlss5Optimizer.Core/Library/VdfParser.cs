using System.Text;

namespace Dlss5Optimizer.Core.Library;

/// <summary>Knoten eines Valve-KeyValues-Dokuments (Text-VDF/ACF).</summary>
public sealed class VdfNode
{
    public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, VdfNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string? this[string key] => Values.GetValueOrDefault(key);

    public VdfNode? Child(string key) => Children.GetValueOrDefault(key);
}

/// <summary>Parser für Steams Text-KeyValues (libraryfolders.vdf, appmanifest_*.acf).</summary>
public static class VdfParser
{
    public static VdfNode Parse(string text)
    {
        var root = new VdfNode();
        int pos = 0;
        ParseInto(root, text, ref pos, topLevel: true);
        return root;
    }

    private static void ParseInto(VdfNode node, string text, ref int pos, bool topLevel)
    {
        while (true)
        {
            var key = NextToken(text, ref pos);
            if (key is null)
            {
                if (!topLevel)
                    throw new FormatException("Unerwartetes Dateiende in VDF");
                return;
            }
            if (key == "}")
                return;

            var value = NextToken(text, ref pos) ?? throw new FormatException($"Wert fehlt für Schlüssel '{key}'");
            if (value == "{")
            {
                var child = new VdfNode();
                ParseInto(child, text, ref pos, topLevel: false);
                node.Children[key] = child;
            }
            else
            {
                node.Values[key] = value;
            }
        }
    }

    /// <returns>Token-Text, "{" bzw. "}" für Klammern, null am Ende.</returns>
    private static string? NextToken(string text, ref int pos)
    {
        while (pos < text.Length)
        {
            char c = text[pos];
            if (char.IsWhiteSpace(c))
            {
                pos++;
            }
            else if (c == '/' && pos + 1 < text.Length && text[pos + 1] == '/')
            {
                while (pos < text.Length && text[pos] != '\n')
                    pos++;
            }
            else
            {
                break;
            }
        }
        if (pos >= text.Length)
            return null;

        char ch = text[pos];
        if (ch is '{' or '}')
        {
            pos++;
            return ch.ToString();
        }

        var sb = new StringBuilder();
        if (ch == '"')
        {
            pos++;
            while (pos < text.Length && text[pos] != '"')
            {
                if (text[pos] == '\\' && pos + 1 < text.Length)
                {
                    pos++;
                    sb.Append(text[pos] switch
                    {
                        'n' => '\n',
                        't' => '\t',
                        _ => text[pos],
                    });
                }
                else
                {
                    sb.Append(text[pos]);
                }
                pos++;
            }
            pos++; // schließendes Anführungszeichen
            return sb.ToString();
        }

        while (pos < text.Length && !char.IsWhiteSpace(text[pos]) && text[pos] is not ('{' or '}' or '"'))
            sb.Append(text[pos++]);
        return sb.ToString();
    }
}
