using System.Globalization;
using Monze.Application;

namespace Monze.Testing.Twin;

/// <summary>
/// Deterministic <see cref="IAiProvider"/>: answers with a short text derived
/// from the input length (see <see cref="ResponseFor"/>). <see cref="ReturnNull"/>
/// makes it return null and <see cref="Throw"/> makes the call fault;
/// <see cref="CallCount"/> counts every call.
/// </summary>
public sealed class TwinAiProvider : IAiProvider
{
    private int _callCount;
    private volatile bool _returnNull;
    private volatile bool _throw;

    /// <summary>When true, CompleteAsync returns null.</summary>
    public bool ReturnNull
    {
        get => _returnNull;
        set => _returnNull = value;
    }

    /// <summary>When true, CompleteAsync returns a faulted task (takes precedence over <see cref="ReturnNull"/>).</summary>
    public bool Throw
    {
        get => _throw;
        set => _throw = value;
    }

    /// <summary>Number of CompleteAsync calls so far.</summary>
    public int CallCount => Volatile.Read(ref _callCount);

    /// <summary>The text CompleteAsync returns for <paramref name="input"/>.</summary>
    public static string ResponseFor(string input)
        => "twin-ai:" + input.Length.ToString(CultureInfo.InvariantCulture);

    public Task<string?> CompleteAsync(string instruction, string input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _callCount);
        if (_throw)
        {
            return Task.FromException<string?>(new InvalidOperationException("Twin AI provider failure."));
        }

        return Task.FromResult<string?>(_returnNull ? null : ResponseFor(input));
    }
}
