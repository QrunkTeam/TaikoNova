using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
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

    static OnlineAuthClient()
    {
        SteamworksAssemblyResolver.Register();
    }

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    private static readonly HttpClient NoRedirectHttp = new(new HttpClientHandler
    {
        AllowAutoRedirect = false
    })
    {
        Timeout = TimeSpan.FromSeconds(15)
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

        string authorizeUrl = await GetDiscordAuthorizeUrlAsync(cancellationToken);
        OpenBrowser(authorizeUrl);

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

        string? error = context.Request.QueryString["error"];
        if (!string.IsNullOrWhiteSpace(error))
        {
            await WriteBrowserClosePageAsync(context.Response,
                "TaikoNova login failed", "Discord returned an error. You can return to the game.", cancellationToken);
            throw new OnlineAuthException($"Discord login failed: {error}");
        }

        string? code = context.Request.QueryString["code"];
        string? state = context.Request.QueryString["state"];
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state))
        {
            await WriteBrowserClosePageAsync(context.Response,
                "TaikoNova login failed", "Discord did not return a login code. You can return to the game.", cancellationToken);
            throw new OnlineAuthException("Discord did not return a login code.");
        }

        await WriteBrowserClosePageAsync(context.Response,
            "TaikoNova login complete", "You can return to the game now.", cancellationToken);

        var exchange = new DiscordExchangeRequest
        {
            Code = code,
            State = state,
            RedirectUri = DiscordRedirectUri
        };

        return await ExchangeAsync($"{BackendBaseUrl}/auth/discord/exchange", exchange, cancellationToken);
    }

    private static async Task<string> GetDiscordAuthorizeUrlAsync(CancellationToken cancellationToken)
    {
        string startUrl = $"{BackendBaseUrl}/auth/discord/start?redirect_uri={Uri.EscapeDataString(DiscordRedirectUri)}";

        HttpResponseMessage response;
        try
        {
            response = await NoRedirectHttp.GetAsync(startUrl, cancellationToken);
        }
        catch (Exception ex)
        {
            throw new OnlineAuthException("Online backend is not reachable. Start the TaikoNova backend and try again.", ex);
        }

        using (response)
        {
            if ((int)response.StatusCode >= 300 && (int)response.StatusCode < 400
                && response.Headers.Location != null)
            {
                return response.Headers.Location.IsAbsoluteUri
                    ? response.Headers.Location.ToString()
                    : new Uri(new Uri(BackendBaseUrl), response.Headers.Location).ToString();
            }

            string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            string detail = TryReadError(responseBody);
            throw new OnlineAuthException(string.IsNullOrWhiteSpace(detail)
                ? $"Discord login could not start. Backend returned status {(int)response.StatusCode}."
                : detail);
        }
    }

    public static async Task<OnlineAuthResult> LoginWithSteamAsync(CancellationToken cancellationToken = default)
    {
        SteamworksAssemblyResolver.Register();

        SteamTicketData ticket;
        try
        {
            ticket = await SteamAuthBridge.GetWebApiTicketAsync(SteamWebApiIdentity, cancellationToken);
        }
        catch (Exception ex) when (SteamworksAssemblyResolver.IsSteamworksAssemblyFailure(ex))
        {
            throw new SteamUnavailableException(
                "Steamworks.NET is missing. Run dotnet restore/build, then launch TaikoNova from the rebuilt output.", ex);
        }

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
        HttpListenerResponse response, string title, string message, CancellationToken cancellationToken)
    {
        string html = $$"""
            <!doctype html>
            <html>
            <head><meta charset="utf-8"><title>{{WebUtility.HtmlEncode(title)}}</title></head>
            <body style="font-family: system-ui; background: #10121a; color: #eef; padding: 32px;">
            <h1>{{WebUtility.HtmlEncode(title)}}</h1>
            <p>{{WebUtility.HtmlEncode(message)}}</p>
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

internal static class SteamworksAssemblyResolver
{
    private static bool _registered;

    public static void Register()
    {
        if (_registered)
            return;

        AssemblyLoadContext.Default.Resolving += Resolve;
        _registered = true;
    }

    public static bool IsSteamworksAssemblyFailure(Exception exception)
    {
        for (Exception? ex = exception; ex != null; ex = ex.InnerException)
        {
            if (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
            {
                string message = ex.Message;
                if (message.Contains("Steamworks.NET", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    private static Assembly? Resolve(AssemblyLoadContext context, AssemblyName assemblyName)
    {
        if (!string.Equals(assemblyName.Name, "Steamworks.NET", StringComparison.OrdinalIgnoreCase))
            return null;

        foreach (string path in GetCandidatePaths(assemblyName))
        {
            if (File.Exists(path))
                return context.LoadFromAssemblyPath(path);
        }

        return null;
    }

    private static IEnumerable<string> GetCandidatePaths(AssemblyName assemblyName)
    {
        string dll = "Steamworks.NET.dll";
        string baseDir = AppContext.BaseDirectory;

        yield return Path.Combine(baseDir, dll);
        yield return Path.Combine(Environment.CurrentDirectory, dll);

        string version = assemblyName.Version == null
            ? "2024.8.0"
            : $"{assemblyName.Version.Major}.{assemblyName.Version.Minor}.{assemblyName.Version.Build}";

        string userNuGet = Environment.GetEnvironmentVariable("NUGET_PACKAGES")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        yield return Path.Combine(userNuGet, "steamworks.net", version, "lib", "netstandard2.1", dll);
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

        try
        {
            while (!_pendingTicket.Task.IsCompleted)
            {
                linked.Token.ThrowIfCancellationRequested();
                SteamAPI.RunCallbacks();
                await Task.Delay(50, linked.Token);
            }
        }
        catch (OperationCanceledException ex)
        {
            throw new SteamUnavailableException("Steam ticket request timed out.", ex);
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
            ValidateLocalSteamRuntime();
            if (!SteamAPI.Init())
            {
                throw new SteamUnavailableException(
                    "Steam is not available. Make sure Steam is running, you are logged in, and steam_appid.txt matches the backend Steam App ID.");
            }
            _initialized = true;
        }
        catch (DllNotFoundException ex)
        {
            throw new SteamUnavailableException($"Missing Steamworks native library: {GetSteamNativeLibraryName()}.", ex);
        }
        catch (Exception ex) when (ex is not SteamUnavailableException)
        {
            throw new SteamUnavailableException("Steam services are unavailable.", ex);
        }
    }

    private static void ValidateLocalSteamRuntime()
    {
        if (!HasSteamAppIdFile() && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SteamAppId"))
            && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SteamGameId")))
        {
            throw new SteamUnavailableException(
                "Missing steam_appid.txt. Put your Steam App ID in steam_appid.txt next to the game or run through Steam.");
        }

        if (!HasSteamNativeLibrary())
            throw new SteamUnavailableException($"Missing Steamworks native library: {GetSteamNativeLibraryName()}.");
    }

    private static bool HasSteamAppIdFile()
    {
        string[] paths =
        {
            Path.Combine(Environment.CurrentDirectory, "steam_appid.txt"),
            Path.Combine(AppContext.BaseDirectory, "steam_appid.txt")
        };
        return paths.Any(File.Exists);
    }

    private static bool HasSteamNativeLibrary()
    {
        string libraryName = GetSteamNativeLibraryName();
        string[] paths =
        {
            Path.Combine(Environment.CurrentDirectory, libraryName),
            Path.Combine(AppContext.BaseDirectory, libraryName),
            Path.Combine(AppContext.BaseDirectory, "runtimes", GetRuntimeNativeFolder(), "native", libraryName)
        };
        return paths.Any(File.Exists);
    }

    private static string GetRuntimeNativeFolder()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return Environment.Is64BitProcess ? "win-x64" : "win-x86";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return "osx";
        return Environment.Is64BitProcess ? "linux-x64" : "linux-x86";
    }

    private static string GetSteamNativeLibraryName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return Environment.Is64BitProcess ? "steam_api64.dll" : "steam_api.dll";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return "libsteam_api.dylib";
        return "libsteam_api.so";
    }

    private static void OnWebTicket(GetTicketForWebApiResponse_t response)
    {
        _pendingTicket?.TrySetResult(response);
    }
}
