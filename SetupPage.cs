using System.Net;
using System.Text.RegularExpressions;

public static partial class SetupPage
{
    public static string Render(string source)
    {
        source = source.Split("## Setup\n")[1].Split("\n## Recovery")[0];
        var fenced = false;
        var body = source.Split('\n').Select(line =>
        {
            if (line.Trim().StartsWith("```", StringComparison.Ordinal))
            {
                fenced = !fenced;
                return fenced ? "<pre><code>" : "</code></pre>";
            }
            if (fenced)
            {
                return WebUtility.HtmlEncode(line);
            }
            if (line.Trim().StartsWith('|'))
            {
                var cells = line.Trim().Split('|')[1..^1].Select(cell => cell.Trim()).ToArray();
                if (cells[0] is "Name" or "Variable" || cells[0].All(character => character == '-'))
                {
                    return "";
                }
                return $"<p><strong>{Inline(cells[0])}</strong>: {Inline(cells[1])}</p>";
            }
            var step = Step().Match(line);
            if (step.Success)
            {
                return $"<h2>{step.Groups[1].Value}. {Inline(step.Groups[2].Value)}</h2><p>{Inline(step.Groups[3].Value)}</p>";
            }
            return string.IsNullOrWhiteSpace(line) ? "" : $"<p>{Inline(line.Trim())}</p>";
        });
        return "<html lang=\"en\"><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\">" +
            "<title>Application Insights setup</title><link rel=\"stylesheet\" href=\"/style.css\">" +
            "<body class=\"setup-reader\"><a href=\"./\">Back to sample</a><h1 id=\"setup\">Connect your already deployed app</h1>" +
            "<p>Manual setup · development/test only. Obtain resource-owner approval before cloud actions.</p>" +
            string.Join("\n", body) +
            "<p><a href=\"https://github.com/AzureAppService/app-service-builder-application-insights\">" +
            "Full README, recovery and optional local details (upstream)</a></p></body></html>";
    }

    private static string Inline(string text)
    {
        text = WebUtility.HtmlEncode(text);
        text = Link().Replace(text, "<a href=\"$2\">$1</a>");
        text = Strong().Replace(text, "<strong>$1</strong>");
        return Code().Replace(text, "<code>$1</code>");
    }

    [GeneratedRegex(@"^(\d+)\. \*\*(.+?)\*\*\s*(.*)$")]
    private static partial Regex Step();
    [GeneratedRegex(@"\[([^\]]+)\]\((https://[^)\s]+)\)")]
    private static partial Regex Link();
    [GeneratedRegex(@"\*\*([^*]+)\*\*")]
    private static partial Regex Strong();
    [GeneratedRegex(@"`([^`]+)`")]
    private static partial Regex Code();
}
