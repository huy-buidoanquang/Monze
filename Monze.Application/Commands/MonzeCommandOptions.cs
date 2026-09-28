namespace Monze.Application.Commands;

public sealed record MonzeCommandOptions(string Prefix, string? Root)
{
    public static MonzeCommandOptions Default { get; } = new("*", MonzeCommandNames.Monze);

    public bool HasRoot => !string.IsNullOrWhiteSpace(Root);

    public string Command(string name, params string[] arguments)
    {
        var commandName = HasRoot ? $"{Root} {name}" : name;
        if (arguments.Length == 0)
        {
            return Prefix + commandName;
        }

        return Prefix + commandName + " " + string.Join(' ', arguments);
    }

    public string DirectCommand(string name, params string[] arguments)
    {
        if (arguments.Length == 0)
        {
            return Prefix + name;
        }

        return Prefix + name + " " + string.Join(' ', arguments);
    }

    public string HelpCommand => HasRoot ? Command(MonzeCommandNames.Help) : Command(MonzeCommandNames.Help);
}
