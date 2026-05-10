using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MCPClient.Core.Models;
using OpenAI;
using OpenAI.Chat;

namespace MCPClient.Core.LlmProviders;

/// <summary>
/// LLM provider that authenticates via ChatGPT / OpenAI account using the
/// OAuth 2.0 Authorization Code + PKCE flow (RFC 7636 / RFC 8252).
///
/// SETUP:
///   Register an OAuth application at https://platform.openai.com to obtain a
///   client_id and set the redirect URI to http://127.0.0.1/callback (loopback).
///   Replace the value of <see cref="ClientId"/> with your registered client_id.
/// </summary>
public class ChatGptProvider : ILlmProvider
{
    // ── OAuth configuration ────────────────────────────────────────────────────
    // Register your app at https://platform.openai.com/apps and paste the
    // client_id here.  Leave the placeholder to build – the flow will fail at
    // runtime if a real id is not supplied.
    private const string ClientId = "YOUR_OPENAI_OAUTH_CLIENT_ID";
    private const string AuthorizationEndpoint = "https://auth.openai.com/authorize";
    private const string TokenEndpoint = "https://auth.openai.com/oauth/token";
    private const string Scopes = "openid email profile";
    private const string OpenAiApiBase = "https://api.openai.com/v1";

    // ── State ──────────────────────────────────────────────────────────────────
    private ChatClient? _client;
    private LlmProviderConfig? _config;
    private string? _accessToken;
    private string? _refreshToken;
    private DateTime _tokenExpiry = DateTime.MinValue;

    // ── ILlmProvider ──────────────────────────────────────────────────────────
    public string Id => "chatgpt";
    public string DisplayName => _config?.DisplayName ?? "ChatGPT";
    public bool IsConfigured => _client != null && !string.IsNullOrEmpty(_accessToken);
    public string CurrentModel => _config?.Model ?? "gpt-4o";

    // ── Events ────────────────────────────────────────────────────────────────
    /// <summary>Raised once sign-in completes successfully.</summary>
    public event Action? AuthenticationCompleted;

    public void SetModel(string modelId)
    {
        if (_config != null) { _config.Model = modelId; InitializeChatClient(); }
    }

    public Task<IReadOnlyList<ModelInfo>> GetAvailableModelsAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ModelInfo> models = new List<ModelInfo>
        {
            new() { Id = "gpt-4o",      DisplayName = "GPT-4o" },
            new() { Id = "gpt-4o-mini", DisplayName = "GPT-4o mini" },
            new() { Id = "gpt-4",       DisplayName = "GPT-4" },
            new() { Id = "o3-mini",     DisplayName = "o3-mini" },
            new() { Id = "o4-mini",     DisplayName = "o4-mini" },
        };
        return Task.FromResult(models);
    }

    public void Configure(LlmProviderConfig config)
    {
        _config = config;
        if (!string.IsNullOrEmpty(config.ApiKey))
        {
            _accessToken  = config.ApiKey;
            _refreshToken = config.RefreshToken;
            InitializeChatClient();
        }
    }

    // ── OAuth PKCE browser flow ───────────────────────────────────────────────

    /// <summary>
    /// Starts the browser-based OAuth 2.0 PKCE sign-in flow.
    /// Opens the default browser to the OpenAI authorization page, then
    /// listens on a loopback port for the redirect callback.
    /// </summary>
    public async Task AuthenticateWithBrowserAsync(CancellationToken cancellationToken = default)
    {
        if (ClientId == "YOUR_OPENAI_OAUTH_CLIENT_ID" || string.IsNullOrEmpty(ClientId))
            throw new InvalidOperationException(
                "A valid OAuth client_id has not been configured for the ChatGPT provider. " +
                "Register an OAuth application at https://platform.openai.com/apps, set the " +
                "redirect URI to http://127.0.0.1/callback, and replace the ClientId constant " +
                "in ChatGptProvider.cs with your registered client_id.");
        var port = FindFreePort();
        var redirectUri = $"http://127.0.0.1:{port}/callback";

        var codeVerifier  = GenerateCodeVerifier();
        var codeChallenge = GenerateCodeChallenge(codeVerifier);
        var state         = GenerateRandomString(16);

        var authUrl = BuildAuthorizationUrl(redirectUri, codeChallenge, state);

        // Open the default browser so the user can log in with their OpenAI account.
        Process.Start(new ProcessStartInfo { FileName = authUrl, UseShellExecute = true });

        // Listen for the redirect callback on loopback.
        var (code, returnedState) = await WaitForCallbackAsync(port, cancellationToken);

        if (returnedState != state)
            throw new InvalidOperationException("OAuth state mismatch – possible CSRF attack.");

        await ExchangeCodeForTokenAsync(code, codeVerifier, redirectUri, cancellationToken);
    }

    // ── Internal helpers ──────────────────────────────────────────────────────

    private string BuildAuthorizationUrl(string redirectUri, string codeChallenge, string state)
    {
        return $"{AuthorizationEndpoint}" +
               $"?client_id={Uri.EscapeDataString(ClientId)}" +
               $"&response_type=code" +
               $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
               $"&code_challenge={Uri.EscapeDataString(codeChallenge)}" +
               $"&code_challenge_method=S256" +
               $"&scope={Uri.EscapeDataString(Scopes)}" +
               $"&state={Uri.EscapeDataString(state)}";
    }

    /// <summary>
    /// Starts a lightweight loopback TCP listener and returns the first
    /// authorization code + state pair that arrives.
    /// </summary>
    private static async Task<(string code, string state)> WaitForCallbackAsync(
        int port, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<(string, string)>();
        cancellationToken.Register(() => tcs.TrySetCanceled());

        // TcpListener does not require URL reservation unlike HttpListener.
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromMinutes(5));

            while (!cts.IsCancellationRequested)
            {
                TcpClient? tcpClient = null;
                try
                {
                    tcpClient = await listener.AcceptTcpClientAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    throw new OperationCanceledException("Sign-in timed out or was cancelled.", cancellationToken);
                }

                using (tcpClient)
                {
                    var stream = tcpClient.GetStream();
                    var buffer = new byte[8192];
                    int bytesRead = await stream.ReadAsync(buffer, cts.Token);

                    // Skip empty or malformed requests (e.g. browser pre-connect probes).
                    if (bytesRead == 0) continue;

                    var requestText = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                    // Parse the first line: GET /callback?code=...&state=... HTTP/1.1
                    var firstLine = requestText.Split('\n', 2)[0].Trim();
                    var parts = firstLine.Split(' ');
                    if (parts.Length < 2) continue;   // not a valid HTTP request line

                    var pathPart = parts[1];
                    var queryString = pathPart.Contains('?')
                        ? pathPart[(pathPart.IndexOf('?') + 1)..]
                        : string.Empty;

                    var query = ParseQueryString(queryString);

                    // Send a minimal HTTP response so the browser closes cleanly.
                    var html = "<html><body style='font-family:sans-serif;text-align:center;padding:60px'>" +
                               "<h2>✓ Signed in to ChatGPT</h2>" +
                               "<p>You can close this tab and return to MCPDesk.</p>" +
                               "</body></html>";
                    var responseBytes = Encoding.UTF8.GetBytes(
                        $"HTTP/1.1 200 OK\r\nContent-Type: text/html\r\n" +
                        $"Content-Length: {html.Length}\r\nConnection: close\r\n\r\n{html}");
                    await stream.WriteAsync(responseBytes, cts.Token);

                    if (query.TryGetValue("error", out var error))
                        throw new InvalidOperationException(
                            $"OAuth error: {error} – {query.GetValueOrDefault("error_description", string.Empty)}");

                    if (query.TryGetValue("code", out var code) && !string.IsNullOrEmpty(code))
                    {
                        query.TryGetValue("state", out var returnedState);
                        return (code, returnedState ?? string.Empty);
                    }
                }
            }
        }
        finally
        {
            listener.Stop();
        }

        throw new OperationCanceledException("Authentication was cancelled.", cancellationToken);
    }

    private async Task ExchangeCodeForTokenAsync(
        string code, string codeVerifier, string redirectUri, CancellationToken cancellationToken)
    {
        using var httpClient = new System.Net.Http.HttpClient();

        var body = new Dictionary<string, string>
        {
            ["grant_type"]    = "authorization_code",
            ["client_id"]     = ClientId,
            ["code"]          = code,
            ["redirect_uri"]  = redirectUri,
            ["code_verifier"] = codeVerifier,
        };

        var response = await httpClient.PostAsync(
            TokenEndpoint,
            new System.Net.Http.FormUrlEncodedContent(body),
            cancellationToken);

        var json = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Token exchange failed ({response.StatusCode}): {json}");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        _accessToken = root.GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("Response contained no access_token.");

        if (root.TryGetProperty("refresh_token", out var rt))
            _refreshToken = rt.GetString();

        if (root.TryGetProperty("expires_in", out var exp))
            _tokenExpiry = DateTime.UtcNow.AddSeconds(exp.GetInt32());

        // Persist both tokens so they can be reloaded on next launch.
        if (_config != null)
        {
            _config.ApiKey       = _accessToken;
            _config.RefreshToken = _refreshToken;
        }

        InitializeChatClient();
        AuthenticationCompleted?.Invoke();
    }

    /// <summary>
    /// Uses the stored refresh_token to obtain a new access_token silently.
    /// </summary>
    private async Task RefreshAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(_refreshToken))
            throw new InvalidOperationException("No refresh token is available. Please sign in again.");

        using var httpClient = new System.Net.Http.HttpClient();
        var body = new Dictionary<string, string>
        {
            ["grant_type"]    = "refresh_token",
            ["client_id"]     = ClientId,
            ["refresh_token"] = _refreshToken,
        };

        var response = await httpClient.PostAsync(
            TokenEndpoint,
            new System.Net.Http.FormUrlEncodedContent(body),
            cancellationToken);

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Token refresh failed ({response.StatusCode}): {json}");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        _accessToken = root.GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("Refresh response contained no access_token.");

        if (root.TryGetProperty("refresh_token", out var rt))
            _refreshToken = rt.GetString();

        if (root.TryGetProperty("expires_in", out var exp))
            _tokenExpiry = DateTime.UtcNow.AddSeconds(exp.GetInt32());

        if (_config != null)
        {
            _config.ApiKey       = _accessToken;
            _config.RefreshToken = _refreshToken;
        }

        InitializeChatClient();
    }

    private void InitializeChatClient()
    {
        if (string.IsNullOrEmpty(_accessToken)) return;
        var model = string.IsNullOrEmpty(_config?.Model) ? "gpt-4o" : _config!.Model;
        var credential = new System.ClientModel.ApiKeyCredential(_accessToken);
        var options = new OpenAIClientOptions { Endpoint = new Uri(OpenAiApiBase) };
        _client = new OpenAIClient(credential, options).GetChatClient(model);
    }

    // ── ChatAsync ─────────────────────────────────────────────────────────────

    public async Task<ChatResponse> ChatAsync(
        IEnumerable<ChatMessage> messages,
        IEnumerable<ToolDefinition>? tools = null,
        CancellationToken cancellationToken = default)
    {
        // Silently refresh the access token when it is close to expiry.
        if (_tokenExpiry != DateTime.MinValue && DateTime.UtcNow >= _tokenExpiry.AddMinutes(-2))
            await RefreshAccessTokenAsync(cancellationToken);

        if (_client == null)
            throw new InvalidOperationException(
                "ChatGPT provider is not configured. Please sign in first.");

        var chatMessages = messages.Select(ConvertMessage).ToList();
        var options = new ChatCompletionOptions();

        if (tools != null)
        {
            foreach (var tool in tools)
            {
                options.Tools.Add(ChatTool.CreateFunctionTool(
                    tool.Name,
                    tool.Description,
                    BinaryData.FromString(tool.ParametersJsonSchema)));
            }
        }

        var completion = await _client.CompleteChatAsync(chatMessages, options, cancellationToken);
        var response   = new ChatResponse();

        foreach (var part in completion.Value.Content)
        {
            if (part.Kind == ChatMessageContentPartKind.Text)
                response.Content += part.Text;
        }

        foreach (var toolCall in completion.Value.ToolCalls)
        {
            response.ToolCalls.Add(new ToolCall
            {
                Id        = toolCall.Id,
                Name      = toolCall.FunctionName,
                Arguments = toolCall.FunctionArguments.ToString()
            });
        }

        return response;
    }

    private static OpenAI.Chat.ChatMessage ConvertMessage(ChatMessage msg) =>
        msg.Role switch
        {
            MessageRole.User      => new UserChatMessage(msg.Content),
            MessageRole.Assistant when msg.ToolCallId != null =>
                new ToolChatMessage(msg.ToolCallId, msg.Content),
            MessageRole.Assistant => new AssistantChatMessage(msg.Content),
            MessageRole.System    => new SystemChatMessage(msg.Content),
            MessageRole.Tool      => new ToolChatMessage(msg.ToolCallId ?? string.Empty, msg.Content),
            _                     => new UserChatMessage(msg.Content)
        };

    // ── PKCE helpers ──────────────────────────────────────────────────────────

    private static string GenerateCodeVerifier()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Base64UrlEncode(bytes);
    }

    private static string GenerateCodeChallenge(string codeVerifier)
    {
        var bytes     = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        return Base64UrlEncode(bytes);
    }

    private static string GenerateRandomString(int length)
    {
        var bytes = RandomNumberGenerator.GetBytes(length);
        return Convert.ToHexString(bytes);
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes)
               .TrimEnd('=')
               .Replace('+', '-')
               .Replace('/', '_');

    // ── Port + query helpers ──────────────────────────────────────────────────

    private static int FindFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static Dictionary<string, string> ParseQueryString(string queryString)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in queryString.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = pair.IndexOf('=');
            if (idx < 0) continue;
            var key   = Uri.UnescapeDataString(pair[..idx].Replace('+', ' '));
            var value = Uri.UnescapeDataString(pair[(idx + 1)..].Replace('+', ' '));
            result[key] = value;
        }
        return result;
    }
}
