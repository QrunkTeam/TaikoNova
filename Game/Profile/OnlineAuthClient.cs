using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Steamworks;

namespace TaikoNova.Game.Profile;

public class OnlineAuthException : Exception
{
    public OnlineAuthException(string message) : base(message) { }
    public OnlineAuthException(string message, Exception inner) : base(message, inner) { }
}

public sealed class SteamUnavailableException : OnlineAuthException
{
    public SteamUnavailableException(string message) : base(message) { }
    public SteamUnavailableException(string message, Exception inner) : base(message, inner) { }
}

public static class OnlineAuthClient
{
    public const string DiscordRedirectUri = "http://127.0.0.1:49457/auth/discord/callback/";

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static async Task<OnlineAuthResult> LoginWithDiscordAsync(CancellationToken cancellationToken = default)
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add(DiscordRedirectUri);

        try
        {
            listener.Start();
        }
        catch (Exception ex)
        {
            throw new OnlineAuthException("Could not start local Discord callback listener.", ex);
        }

        string startUrl = $"{BackendBaseUrl}/auth/discord/start?redirect_uri={Uri.EscapeDataString(DiscordRedirectUri)}";
        OpenBrowser(startUrl);

        HttpListenerContext context;
        try
        {
            context = await listener.GetContextAsync()
                .WaitAsync(TimeSpan.FromMinutes(3), cancellationToken);
        }
        catch (TimeoutException ex)
        {
            throw new OnlineAuthException("Discord login timed out.", ex);
        }

        await WriteBrowserClosePageAsync(context.Response, cancellationToken);

        string? error = context.Request.QueryString["error"];
        if (!string.IsNullOrWhiteSpace(error))
            throw new OnlineAuthException($"Discord login failed: {error}");

        string? code = context.Request.QueryString["code"];
        string? state = context.Request.QueryString["state"];
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state))
            throw new OnlineAuthException("Discord did not return a login code.");

        var exchange = new DiscordExchangeRequest
        {
            Code = code,
            State = state,
            RedirectUri = DiscordRedirectUri
        };

        return await ExchangeAsync($"{BackendBaseUrl}/auth/discord/exchange", exchange, cancellationToken);
    }

    public static async Task<OnlineAuthResult> LoginWithSteamAsync(CancellationToken cancellationToken = default)
    {
        var ticket = await SteamAuthBridge.GetWebApiTicketAsync(SteamWebApiIdentity, cancellationToken);
        var exchange = new SteamExchangeRequest
        {
            TicketBase64 = Convert.ToBase64String(ticket.Ticket),
            SteamId = ticket.SteamId,
            PersonaName = ticket.PersonaName
        };

        return await ExchangeAsync($"{BackendBaseUrl}/auth/steam/exchange", exchange, cancellationToken);
    }

    private static async Task<OnlineAuthResult> ExchangeAsync(
        string url, object request, CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(request, JsonOptions);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await Http.PostAsync(url, content, cancellationToken);
        string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            string detail = TryReadError(responseBody);
            throw new OnlineAuthException(string.IsNullOrWhiteSpace(detail)
                ? $"Online auth failed with status {(int)response.StatusCode}."
                : detail);
        }

        var dto = JsonSerializer.Deserialize<AuthResponseDto>(responseBody, JsonOptions)
            ?? throw new OnlineAuthException("Backend returned an empty auth response.");

        byte[]? avatar = null;
        if (!string.IsNullOrWhiteSpace(dto.AvatarImageBase64))
        {
            try { avatar = Convert.FromBase64String(dto.AvatarImageBase64); }
            catch { avatar = null; }
        }

        return new OnlineAuthResult
        {
            Provider = dto.Provider,
            AccountId = dto.AccountId,
            Username = dto.Username,
            AvatarUrl = dto.AvatarUrl,
            AvatarContentType = dto.AvatarContentType,
            AvatarImage = avatar,
            SessionToken = dto.SessionToken,
            ExpiresAt = dto.ExpiresAt
        };
    }

    private static string BackendBaseUrl
        => (Environment.GetEnvironmentVariable("TAIKONOVA_BACKEND_URL") ?? "http://127.0.0.1:8787")
            .TrimEnd('/');

    private static string SteamWebApiIdentity
        => Environment.GetEnvironmentVariable("STEAM_WEB_API_IDENTITY") ?? "TaikoNovaBackend";

    private static void OpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            throw new OnlineAuthException("Could not open your browser for login.", ex);
        }
    }

    private static async Task WriteBrowserClosePageAsync(
        HttpListenerResponse response, CancellationToken cancellationToken)
    {
        const string html = """
            <!doctype html>
            <html>
            <head><meta charset="utf-8"><title>TaikoNova Login</title></head>
            <body style="font-family: system-ui; background: #10121a; color: #eef; padding: 32px;">
            <h1>TaikoNova login complete</h1>
            <p>You can return to the game now.</p>
            </body>
            </html>
            """;
        byte[] body = Encoding.UTF8.GetBytes(html);
        response.StatusCode = 200;
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = body.Length;
        await response.OutputStream.WriteAsync(body, cancellationToken);
        response.Close();
    }

    private static string TryReadError(string json)
    {
        try
        {
            var error = JsonSerializer.Deserialize<ApiErrorDto>(json, JsonOptions);
            return error?.Error ?? "";
        }
        catch
        {
            return "";
        }
    }

    private sealed class DiscordExchangeRequest
    {
        [JsonPropertyName("code")]
        public string Code { get; set; } = "";

        [JsonPropertyName("state")]
        public string State { get; set; } = "";

        [JsonPropertyName("redirect_uri")]
        public string RedirectUri { get; set; } = "";
    }

    private sealed class SteamExchangeRequest
    {
        [JsonPropertyName("ticket_base64")]
        public string TicketBase64 { get; set; } = "";

        [JsonPropertyName("steam_id")]
        public string SteamId { get; set; } = "";

        [JsonPropertyName("persona_name")]
        public string PersonaName { get; set; } = "";
    }

    private sealed class AuthResponseDto
    {
        [JsonPropertyName("provider")]
        public string Provider { get; set; } = "";

        [JsonPropertyName("account_id")]
        public string AccountId { get; set; } = "";

        [JsonPropertyName("username")]
        public string Username { get; set; } = "";

        [JsonPropertyName("avatar_url")]
        public string AvatarUrl { get; set; } = "";

        [JsonPropertyName("avatar_image_base64")]
        public string AvatarImageBase64 { get; set; } = "";

        [JsonPropertyName("avatar_content_type")]
        public string AvatarContentType { get; set; } = "";

        [JsonPropertyName("session_token")]
        public string SessionToken { get; set; } = "";

        [JsonPropertyName("expires_at")]
        public DateTime ExpiresAt { get; set; }
    }

    private sealed class ApiErrorDto
    {
        [JsonPropertyName("error")]
        public string Error { get; set; } = "";
    }
}

internal sealed class SteamTicketData
{
    public byte[] Ticket { get; init; } = Array.Empty<byte>();
    public string SteamId { get; init; } = "";
    public string PersonaName { get; init; } = "";
}

internal static class SteamAuthBridge
{
    private static bool _initialized;
    private static Callback<GetTicketForWebApiResponse_t>? _webTicketCallback;
    private static TaskCompletionSource<GetTicketForWebApiResponse_t>? _pendingTicket;

    public static async Task<SteamTicketData> GetWebApiTicketAsync(
        string identity, CancellationToken cancellationToken)
    {
        EnsureInitialized();

        _pendingTicket = new TaskCompletionSource<GetTicketForWebApiResponse_t>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _webTicketCallback ??= Callback<GetTicketForWebApiResponse_t>.Create(OnWebTicket);

        HAuthTicket handle = SteamUser.GetAuthTicketForWebApi(identity);
        if (handle == HAuthTicket.Invalid)
            throw new SteamUnavailableException("Steam services are unavailable.");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeout.Token);

        while (!_pendingTicket.Task.IsCompleted)
        {
            linked.Token.ThrowIfCancellationRequested();
            SteamAPI.RunCallbacks();
            await Task.Delay(50, linked.Token);
        }

        var response = await _pendingTicket.Task;
        if (response.m_eResult != EResult.k_EResultOK)
            throw new SteamUnavailableException("Steam services are unavailable.");

        byte[] ticket = response.m_rgubTicket.Take(response.m_cubTicket).ToArray();
        if (ticket.Length == 0)
            throw new SteamUnavailableException("Steam services are unavailable.");

        return new SteamTicketData
        {
            Ticket = ticket,
            SteamId = SteamUser.GetSteamID().m_SteamID.ToString(),
            PersonaName = SteamFriends.GetPersonaName()
        };
    }

    private static void EnsureInitialized()
    {
        if (_initialized)
            return;

        try
        {
            if (!SteamAPI.Init())
                throw new SteamUnavailableException("Steam services are unavailable.");
            _initialized = true;
        }
        catch (DllNotFoundException ex)
        {
            throw new SteamUnavailableException("Steam services are unavailable.", ex);
        }
        catch (Exception ex) when (ex is not SteamUnavailableException)
        {
            throw new SteamUnavailableException("Steam services are unavailable.", ex);
        }
    }

    private static void OnWebTicket(GetTicketForWebApiResponse_t response)
    {
        _pendingTicket?.TrySetResult(response);
    }
}
