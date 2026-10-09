namespace Monze;

internal static class HttpPayloadLimits
{
    // A summary response carries the full transcript; a long meeting exceeds 512 KiB.
    internal const int TranscriptResponseBytes = 8 * 1024 * 1024;
    internal const int AiResponseBytes = 2 * 1024 * 1024;
}
