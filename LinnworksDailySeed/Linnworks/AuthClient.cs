using System.Text;
using System.Text.Json;
using LinnworksDailySeed.Configuration;
using LinnworksDailySeed.Models;
using Microsoft.Extensions.Options;

namespace LinnworksDailySeed.Linnworks;

public sealed class AuthClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly LinnworksOptions _options;
    private readonly ILogger<AuthClient> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private string? _sessionToken;
    private string? _server;
    private DateTimeOffset _sessionAcquiredAt = DateTimeOffset.MinValue;

    public AuthClient(
        IHttpClientFactory httpClientFactory,
        IOptions<LinnworksOptions> options,
        ILogger<AuthClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<(string Token, string Server)> GetSessionAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!string.IsNullOrWhiteSpace(_sessionToken)
                && !string.IsNullOrWhiteSpace(_server)
                && DateTimeOffset.UtcNow - _sessionAcquiredAt < TimeSpan.FromMinutes(15))
            {
                return (_sessionToken, _server);
            }

            return await AuthorizeAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<(string Token, string Server)> RefreshSessionAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await AuthorizeAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<(string Token, string Server)> AuthorizeAsync(CancellationToken cancellationToken)
    {
        ValidateCredentials();

        var body =
            $"applicationId={Uri.EscapeDataString(_options.ApplicationId)}" +
            $"&applicationSecret={Uri.EscapeDataString(_options.ApplicationSecret)}" +
            $"&token={Uri.EscapeDataString(_options.PermanentToken)}";

        var client = _httpClientFactory.CreateClient(nameof(AuthClient));
        using var content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded");
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.linnworks.net/api/Auth/AuthorizeByApplication")
        {
            Content = content
        };

        _logger.LogInformation("Authorizing Linnworks session via AuthorizeByApplication.");
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"AuthorizeByApplication failed ({(int)response.StatusCode}): {responseBody}");
        }

        var session = JsonSerializer.Deserialize<BaseSession>(responseBody, JsonOptions)
            ?? throw new InvalidOperationException("AuthorizeByApplication returned an empty session.");

        if (string.IsNullOrWhiteSpace(session.Token) || string.IsNullOrWhiteSpace(session.Server))
        {
            throw new InvalidOperationException("AuthorizeByApplication response is missing Token or Server.");
        }

        _sessionToken = session.Token;
        _server = session.Server.TrimEnd('/');
        _sessionAcquiredAt = DateTimeOffset.UtcNow;
        _logger.LogInformation(
            "Linnworks session acquired for {Email}. Server={Server}",
            session.Email ?? "(unknown)",
            _server);

        return (_sessionToken, _server);
    }

    private void ValidateCredentials()
    {
        if (string.IsNullOrWhiteSpace(_options.ApplicationId)
            || string.IsNullOrWhiteSpace(_options.ApplicationSecret)
            || string.IsNullOrWhiteSpace(_options.PermanentToken)
            || !Guid.TryParse(_options.ApplicationId, out _)
            || !Guid.TryParse(_options.ApplicationSecret, out _)
            || !Guid.TryParse(_options.PermanentToken, out _))
        {
            throw new InvalidOperationException(
                "Linnworks ApplicationId, ApplicationSecret, and PermanentToken must be valid GUIDs in appsettings.Local.json.");
        }
    }
}
