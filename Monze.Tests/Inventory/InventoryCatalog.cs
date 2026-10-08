using System.Reflection;
using System.Text.RegularExpressions;
using Monze.Application;
using Monze.Application.Commands;
using Monze.Infrastructure.Persistence;
using Monze.Testing;
using Monze.Ui;

namespace Monze.Tests.Inventory;

internal sealed record InventoryItem(string Id, string Kind, string Source);

/// <summary>
/// Extracts the testable surface of Monze from code: command names, button
/// ids, message catalog entries, application ports, configuration keys, log
/// templates, tables, migrations, metrics, hosted services, SDK client members
/// and outbound HTTP paths. Every item must be declared by a requirement or an
/// exclusion in tests/traceability (see InventoryTraceabilityTests).
/// </summary>
internal static partial class InventoryCatalog
{
    private static readonly string[] ProductionRoots =
        ["Hosting", "Features", "Infrastructure", "Ui", "Monze.Domain", "Monze.Application", "Monze.Infrastructure", "Program.cs"];

    public static IReadOnlyList<InventoryItem> Extract()
    {
        var items = new Dictionary<string, InventoryItem>(StringComparer.Ordinal);
        void Add(string kind, string name, string source)
            => items.TryAdd($"{kind}:{name}", new InventoryItem($"{kind}:{name}", kind, source));

        foreach (var value in ConstantValues(typeof(MonzeCommandNames)))
        {
            Add("cmd", value, nameof(MonzeCommandNames));
        }

        foreach (var type in new[] { typeof(MonzeButtonId), typeof(MeetingButtonId) })
        {
            foreach (var value in ConstantValues(type))
            {
                Add("btn", value, type.Name);
            }
        }

        foreach (var member in typeof(MonzeMessages).GetMembers(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            if (member is FieldInfo or PropertyInfo || member is MethodInfo { IsSpecialName: false })
            {
                Add("msg", member.Name, nameof(MonzeMessages));
            }
        }

        foreach (var port in typeof(MonzeApp).Assembly.GetExportedTypes().Where(static type => type.IsInterface))
        {
            foreach (var method in port.GetMethods().Where(static method => !method.IsSpecialName))
            {
                Add("port", $"{PortName(port)}.{method.Name}", port.Name);
            }
        }

        foreach (var (file, text) in ProductionSources())
        {
            foreach (Match match in ConfigurationKey().Matches(text))
            {
                Add("cfg", match.Groups["key"].Value, file);
            }

            foreach (Match match in LogTemplate().Matches(text))
            {
                Add("log", $"{match.Groups["level"].Value}:{match.Groups["template"].Value}", file);
            }

            foreach (Match match in MetricName().Matches(text))
            {
                Add("metric", match.Groups["name"].Value, file);
            }

            foreach (Match match in HostedService().Matches(text))
            {
                Add("hosted", match.Groups["type"].Value, file);
            }

            foreach (Match match in ClientMember().Matches(text))
            {
                Add("sdk", $"client.{match.Groups["member"].Value}", file);
            }

            foreach (Match match in ContextMember().Matches(text))
            {
                Add("sdk", $"context.{match.Groups["member"].Value}", file);
            }

            foreach (Match match in HttpPath().Matches(text))
            {
                Add("http", HttpPlaceholder().Replace(match.Groups["path"].Value, "{}"), file);
            }
        }

        var tables = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (name, sql) in Migrations())
        {
            Add("migration", name, "Monze.Infrastructure/Persistence/Migrations");
            foreach (Match match in TableStatement().Matches(sql))
            {
                var table = match.Groups["table"].Value.ToLowerInvariant();
                if (match.Groups["verb"].Value.StartsWith("DROP", StringComparison.OrdinalIgnoreCase))
                {
                    tables.Remove(table);
                }
                else
                {
                    tables.Add(table);
                }
            }
        }

        foreach (var table in tables)
        {
            Add("table", table, "migrations");
        }

        return items.Values.OrderBy(static item => item.Id, StringComparer.Ordinal).ToList();
    }

    private static IEnumerable<string> ConstantValues(Type type)
        => type.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(static field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(static field => (string)field.GetRawConstantValue()!)
            .Distinct(StringComparer.Ordinal);

    private static string PortName(Type port)
        => port.IsGenericType ? port.Name[..port.Name.IndexOf('`', StringComparison.Ordinal)] : port.Name;

    private static IEnumerable<(string File, string Text)> ProductionSources()
    {
        var root = RepositoryPaths.Root;
        foreach (var entry in ProductionRoots)
        {
            var path = Path.Combine(root, entry);
            var files = File.Exists(path)
                ? [path]
                : Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories);
            foreach (var file in files)
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (relative.Contains("/bin/", StringComparison.Ordinal) || relative.Contains("/obj/", StringComparison.Ordinal))
                {
                    continue;
                }

                yield return (relative, File.ReadAllText(file));
            }
        }
    }

    private static IEnumerable<(string Name, string Sql)> Migrations()
    {
        var assembly = typeof(PostgresMigrator).Assembly;
        foreach (var resource in assembly.GetManifestResourceNames()
                     .Where(static name => name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
                     .Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            var name = Path.GetFileNameWithoutExtension(resource);
            yield return (name[(name.LastIndexOf('.') + 1)..], reader.ReadToEnd());
        }
    }

    [GeneratedRegex("\"(?<key>(?:Monze|Mezon):[A-Za-z]+(?::[A-Za-z]+)*)\"")]
    private static partial Regex ConfigurationKey();

    [GeneratedRegex("\\.Log(?<level>Trace|Debug|Information|Warning|Error|Critical)\\((?<pre>[^;\"]*?)\"(?<template>(?:[^\"\\\\]|\\\\.)*)\"")]
    private static partial Regex LogTemplate();

    [GeneratedRegex("\"(?<name>monze\\.[a-z0-9_]+(?:\\.[a-z0-9_]+)+)\"")]
    private static partial Regex MetricName();

    [GeneratedRegex("AddHostedService<(?<type>\\w+)>")]
    private static partial Regex HostedService();

    [GeneratedRegex("\\bclient\\.(?<member>[A-Z]\\w*)")]
    private static partial Regex ClientMember();

    [GeneratedRegex("\\bcontext\\.(?<member>[A-Z]\\w*Async)\\(")]
    private static partial Regex ContextMember();

    [GeneratedRegex("\\$?\"(?<path>(?:api/v2|v1)/[^\"]+)\"")]
    private static partial Regex HttpPath();

    [GeneratedRegex("\\{[^}]*\\}")]
    private static partial Regex HttpPlaceholder();

    [GeneratedRegex("(?<verb>CREATE\\s+TABLE(?:\\s+IF\\s+NOT\\s+EXISTS)?|DROP\\s+TABLE(?:\\s+IF\\s+EXISTS)?)\\s+(?<table>[A-Za-z_][A-Za-z0-9_]*)", RegexOptions.IgnoreCase)]
    private static partial Regex TableStatement();
}
