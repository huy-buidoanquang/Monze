using System.Buffers;
using System.Text;

namespace Monze;

internal static class BoundedHttpContent
{
    internal static async Task<string?> ReadStringAsync(
        HttpContent content,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is long length && length > maxBytes)
        {
            return null;
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        var buffer = ArrayPool<byte>.Shared.Rent(Math.Min(81920, maxBytes));
        using var payload = new MemoryStream(Math.Min(maxBytes, 16384));
        var total = 0;
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken);
                if (read == 0)
                {
                    return Encoding.UTF8.GetString(payload.GetBuffer(), 0, (int)payload.Length);
                }

                total += read;
                if (total > maxBytes)
                {
                    return null;
                }

                payload.Write(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
