using System.Net;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Mezon.Net.Abstractions;
using Mezon.Net.Core;

namespace Monze.Simulator;

/// <summary>
/// The simulated gateway REST endpoint, installed through
/// <c>MezonClientOptions.RestClientProvider</c>. Mezon.Net 1.6.2 uses REST
/// only for the bot login (POST /v2/apps/authenticate/token with Basic
/// auth and JSON {"account":{"appid","token"}}, answered with a protobuf
/// mezon.api.Session); every other path, method or base URL fails closed.
/// </summary>
public sealed class SimRestClient : IRestClient
{
    private readonly MezonSimulator _simulator;
    private readonly Dictionary<string, string> _headers = new(StringComparer.OrdinalIgnoreCase);

    internal SimRestClient(MezonSimulator simulator, string baseUrl)
    {
        _simulator = simulator;
        BaseUrl = baseUrl;
    }

    /// <summary>The base URL the SDK created this client for.</summary>
    public string BaseUrl { get; }

    public void SetHeader(string key, string value)
    {
        lock (_headers)
        {
            _headers[key] = value;
        }
    }

    public void SetCancelToken(CancellationToken cancelToken)
    {
        // Requests complete synchronously or by fault; nothing to cancel.
    }

    public Task<HttpResponse> SendAsync(
        string method,
        string endpoint,
        CancellationToken cancelToken,
        bool headerOnly = false,
        IEnumerable<KeyValuePair<string, IEnumerable<string>>>? requestHeaders = null)
        => HandleAsync(method, endpoint, json: null, requestHeaders, cancelToken);

    public Task<HttpResponse> SendAsync(
        string method,
        string endpoint,
        string json,
        CancellationToken cancelToken,
        bool headerOnly = false,
        IEnumerable<KeyValuePair<string, IEnumerable<string>>>? requestHeaders = null)
        => HandleAsync(method, endpoint, json, requestHeaders, cancelToken);

    public Task<HttpResponse> SendAsync(
        string method,
        string endpoint,
        IReadOnlyDictionary<string, object> multipartParams,
        CancellationToken cancelToken,
        bool headerOnly = false,
        IEnumerable<KeyValuePair<string, IEnumerable<string>>>? requestHeaders = null)
    {
        _simulator.Recorder.RecordUnmodelled(0, $"{method} {endpoint}", "Multipart REST request.");
        return Task.FromResult(Json(HttpStatusCode.NotImplemented, "not modelled by the Monze simulator"));
    }

    public void Dispose()
    {
    }

    private async Task<HttpResponse> HandleAsync(
        string method,
        string endpoint,
        string? json,
        IEnumerable<KeyValuePair<string, IEnumerable<string>>>? requestHeaders,
        CancellationToken cancelToken)
    {
        var operation = $"{method} {endpoint}";
        if (!string.Equals(BaseUrl.TrimEnd('/'), _simulator.Options.GatewayBasePath, StringComparison.OrdinalIgnoreCase))
        {
            _simulator.Recorder.RecordViolation(0, operation, $"REST call to {BaseUrl}, which the simulator does not serve.");
            throw new HttpRequestException($"The simulated platform does not serve {BaseUrl}.");
        }

        if (!string.Equals(operation, SimOperations.Authenticate, StringComparison.Ordinal))
        {
            _simulator.Recorder.RecordUnmodelled(0, operation, "REST request.");
            return Json(HttpStatusCode.NotImplemented, "not modelled by the Monze simulator");
        }

        var (delay, terminal) = _simulator.Faults.TakeOperation(SimOperations.Authenticate);
        if (delay is not null)
        {
            await Task.Delay(delay.Delay, cancelToken).ConfigureAwait(false);
        }

        if (terminal is { Kind: SimFaultKind.Error } error)
        {
            Record(error.Code, error.Kind);
            return Json(ToHttpStatus(error.Code), error.Detail ?? "simulated failure");
        }

        if (terminal is not null)
        {
            Record(MezonStatusCode.Unavailable, terminal.Kind);
            throw new HttpRequestException("The simulated gateway dropped the connection.");
        }

        if (!HasBasicAuth(requestHeaders))
        {
            _simulator.Recorder.RecordViolation(0, operation, "Login without Basic server-key authorization.");
            Record(MezonStatusCode.Unauthenticated, null);
            return Json(HttpStatusCode.Unauthorized, "missing server key");
        }

        if (!CredentialsMatch(json))
        {
            Record(MezonStatusCode.Unauthenticated, null);
            return Json(HttpStatusCode.Unauthorized, "invalid app credentials");
        }

        var session = _simulator.IssueSession();
        Record(MezonStatusCode.Ok, null);
        return new HttpResponse(
            HttpStatusCode.OK,
            new Dictionary<string, string> { ["Content-Type"] = "application/x-protobuf" },
            new MemoryStream(session.ToByteArray()));
    }

    private void Record(MezonStatusCode status, SimFaultKind? fault)
        => _simulator.Recorder.Record(new SimAction
        {
            Kind = SimActionKind.Authenticate,
            Operation = SimOperations.Authenticate,
            ResponseCode = (int)status,
            Fault = fault
        });

    private bool HasBasicAuth(IEnumerable<KeyValuePair<string, IEnumerable<string>>>? requestHeaders)
    {
        if (requestHeaders is null)
        {
            return false;
        }

        foreach (var header in requestHeaders)
        {
            if (!header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var value in header.Value)
            {
                if (!value.StartsWith("Basic ", StringComparison.Ordinal))
                {
                    continue;
                }

                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(value["Basic ".Length..]));
                if (string.Equals(decoded, _simulator.ServerKey + ":", StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private bool CredentialsMatch(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("account", out var account)
                || account.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var bot = _simulator.World.Bot;
            return account.TryGetProperty("appid", out var appId)
                && appId.ValueKind == JsonValueKind.String
                && appId.GetString() == bot.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)
                && account.TryGetProperty("token", out var token)
                && token.ValueKind == JsonValueKind.String
                && token.GetString() == bot.Token;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static HttpStatusCode ToHttpStatus(MezonStatusCode code) => code switch
    {
        MezonStatusCode.Unauthenticated => HttpStatusCode.Unauthorized,
        MezonStatusCode.PermissionDenied => HttpStatusCode.Forbidden,
        MezonStatusCode.NotFound => HttpStatusCode.NotFound,
        MezonStatusCode.InvalidArgument => HttpStatusCode.BadRequest,
        MezonStatusCode.ResourceExhausted => HttpStatusCode.TooManyRequests,
        MezonStatusCode.Unavailable => HttpStatusCode.ServiceUnavailable,
        _ => HttpStatusCode.InternalServerError
    };

    private static HttpResponse Json(HttpStatusCode status, string message)
        => new(
            status,
            new Dictionary<string, string> { ["Content-Type"] = "application/json" },
            new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object> { ["code"] = (int)status, ["message"] = message })));
}
