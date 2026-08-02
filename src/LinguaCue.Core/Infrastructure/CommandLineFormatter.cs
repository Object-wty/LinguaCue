namespace LinguaCue.Infrastructure;

public static class CommandLineFormatter
{
    public static string Format(string fileName, IEnumerable<string> arguments) =>
        string.Join(" ", new[] { fileName }.Concat(arguments.Select(Quote)));

    private static string Quote(string value)
    {
        if (value.Length > 0 && value.All(character =>
                !char.IsWhiteSpace(character) && character is not '"'))
        {
            return value;
        }

        return $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
    }
}
