using System.Globalization;
using CsCheck;

namespace Monze.Tests.Property.Harness;

/// <summary>
/// Culture is a generated dimension: parsing and formatting must not depend
/// on the thread culture of the host.
/// </summary>
internal static class Cultures
{
    public static readonly string[] Names = ["invariant", "vi-VN", "tr-TR", "ar-SA"];

    public static Gen<string> Gen { get; } = CsCheck.Gen.OneOfConst(Names);

    public static TResult Run<TResult>(string name, Func<TResult> action)
    {
        var previous = CultureInfo.CurrentCulture;
        var previousUi = CultureInfo.CurrentUICulture;
        var culture = name == "invariant" ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo(name);
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        try
        {
            return action();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
            CultureInfo.CurrentUICulture = previousUi;
        }
    }
}
