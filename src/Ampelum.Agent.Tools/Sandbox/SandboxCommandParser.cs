namespace SignalGrid.Ampelum.Agent.Tools;

public static class SandboxCommandParser
{
    public static string[] Tokenize(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return [];

        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuote = false;

        foreach (var ch in command.Trim())
        {
            if (ch == '"')
            {
                inQuote = !inQuote;
                continue;
            }

            if (char.IsWhiteSpace(ch) && !inQuote)
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }

            current.Append(ch);
        }

        if (current.Length > 0)
            tokens.Add(current.ToString());

        return tokens.ToArray();
    }
}
