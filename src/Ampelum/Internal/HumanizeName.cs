using System.Text;

namespace SignalGrid.Ampelum.Internal;

internal static class HumanizeName
{
    /// <summary>
    /// Split a PascalCase identifier into space-separated words, preserving
    /// consecutive uppercase sequences as single acronyms.
    /// "FirstName" â†’ "First Name"; "URLField" â†’ "URL Field"; "HTTPRequest" â†’ "HTTP Request".
    /// </summary>
    public static string Split(string name)
    {
        if (name.Length <= 1) return name;
        var sb = new StringBuilder(name.Length + 4);
        for (int i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (i > 0 && ShouldInsertSpace(name, i))
                sb.Append(' ');
            sb.Append(c);
        }
        return sb.ToString();
    }

    static bool ShouldInsertSpace(string s, int i)
    {
        var prev = s[i - 1];
        var cur = s[i];
        if (char.IsLower(prev) && char.IsUpper(cur)) return true;
        if (i + 1 < s.Length && char.IsUpper(prev) && char.IsUpper(cur) && char.IsLower(s[i + 1])) return true;
        return false;
    }
}
