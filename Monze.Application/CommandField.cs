namespace Monze.Application;

/// <summary>
/// A logical value shown as one embed field. Keeping this model outside the
/// SDK UI layer lets command handlers return structured output without
/// concatenating unrelated values into one message string.
/// </summary>
public sealed record CommandField(string Name, string Value, bool Inline = false);
