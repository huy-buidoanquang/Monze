using Monze.Application;

namespace Monze.Ui;

public static class MonzeEmbedColors
{
    private static readonly string[] Informational =
    [
        "#2563EB",
        "#7C3AED",
        "#0891B2",
        "#0F766E",
        "#C026D3",
        "#4F46E5"
    ];

    public const string Success = "#16A34A";
    public const string Warning = "#D97706";
    public const string Error = "#DC2626";

    public static string For(MonzeTone tone)
        => tone switch
        {
            MonzeTone.Ok => Success,
            MonzeTone.Warn => Warning,
            MonzeTone.Error => Error,
            _ => Informational[Random.Shared.Next(Informational.Length)]
        };

    public static bool IsInformational(string color)
        => Array.IndexOf(Informational, color) >= 0;
}
