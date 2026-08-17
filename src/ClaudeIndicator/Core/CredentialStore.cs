using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeIndicator.Core;

public class ClaudeCredentials
{
    public string AccessToken { get; set; } = "";
    public string? RefreshToken { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public string? SubscriptionType { get; set; }
    public string Source { get; set; } = "";

    public bool IsExpired => ExpiresAt != null && DateTimeOffset.UtcNow >= ExpiresAt.Value.AddMinutes(-2);

    public string Describe()
    {
        var plan = string.IsNullOrWhiteSpace(SubscriptionType) ? "" : $" · plano {SubscriptionType}";
        return Source + plan;
    }
}

/// <summary>
/// Lê o token OAuth que o Claude Code guarda em %USERPROFILE%\.claude\.credentials.json
/// (ou um token colado manualmente) e renova quando necessário.
/// O arquivo do Claude Code NUNCA é modificado: tokens renovados ficam em um cache próprio.
/// </summary>
public class CredentialStore
{
    // client_id público do Claude Code (usado apenas para renovar o token do próprio usuário)
    private const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    private const string TokenUrl = "https://console.anthropic.com/v1/oauth/token";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(25) };

    private ClaudeCredentials? _cache;
    private readonly object _lock = new();

    public static string ClaudeCodeCredentialsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");

    private static string TokenCachePath => Path.Combine(AppSettings.DataDir, "token-cache.json");

    public static bool ClaudeCodeDetected => File.Exists(ClaudeCodeCredentialsPath);

    public void Invalidate()
    {
        lock (_lock) _cache = null;
    }

    public async Task<ClaudeCredentials?> GetAsync(AppSettings settings, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_cache != null && !_cache.IsExpired) return _cache;
        }

        // 1) Token manual
        if (settings.CredentialSource == "Manual")
        {
            var manual = (settings.ManualAccessToken ?? "").Trim();
            if (manual.Length == 0) return null;
            var cred = new ClaudeCredentials { AccessToken = manual, Source = "Token manual" };
            lock (_lock) _cache = cred;
            return cred;
        }

        // 2) Variável de ambiente (claude setup-token)
        var env = Environment.GetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN");
        if (!string.IsNullOrWhiteSpace(env))
        {
            var cred = new ClaudeCredentials { AccessToken = env.Trim(), Source = "CLAUDE_CODE_OAUTH_TOKEN" };
            lock (_lock) _cache = cred;
            return cred;
        }

        // 3) Arquivo do Claude Code
        var file = ReadClaudeCodeFile();
        if (file == null) return null;

        if (!file.IsExpired && !string.IsNullOrEmpty(file.AccessToken))
        {
            lock (_lock) _cache = file;
            return file;
        }

        // Token expirado: tenta o cache local de renovação
        var cached = ReadTokenCache(file.RefreshToken);
        if (cached != null && !cached.IsExpired)
        {
            cached.SubscriptionType = file.SubscriptionType;
            lock (_lock) _cache = cached;
            return cached;
        }

        var refreshed = await RefreshAsync(file, ct);
        if (refreshed != null)
        {
            lock (_lock) _cache = refreshed;
            return refreshed;
        }

        // Última tentativa: devolve o token mesmo expirado (o servidor decide)
        if (!string.IsNullOrEmpty(file.AccessToken))
        {
            lock (_lock) _cache = file;
            return file;
        }
        return null;
    }

    /// <summary>Força renovação (usado quando a API devolve 401).</summary>
    public async Task<ClaudeCredentials?> ForceRefreshAsync(AppSettings settings, CancellationToken ct = default)
    {
        Invalidate();
        if (settings.CredentialSource == "Manual") return null;

        var file = ReadClaudeCodeFile();
        if (file?.RefreshToken == null) return null;

        var refreshed = await RefreshAsync(file, ct);
        if (refreshed != null) lock (_lock) _cache = refreshed;
        return refreshed;
    }

    // ------------------------------------------------------------------

    public static ClaudeCredentials? ReadClaudeCodeFile()
    {
        try
        {
            var path = ClaudeCodeCredentialsPath;
            if (!File.Exists(path)) return null;

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var doc = JsonDocument.Parse(fs);
            var root = doc.RootElement;

            var node = root;
            if (TryProp(root, out var oauth, "claudeAiOauth", "claude_ai_oauth", "oauth", "claudeAi"))
                node = oauth;

            if (!TryProp(node, out var atEl, "accessToken", "access_token", "token")) return null;
            var accessToken = atEl.ValueKind == JsonValueKind.String ? atEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(accessToken)) return null;

            var cred = new ClaudeCredentials
            {
                AccessToken = accessToken!.Trim(),
                Source = "Claude Code"
            };

            if (TryProp(node, out var rtEl, "refreshToken", "refresh_token") && rtEl.ValueKind == JsonValueKind.String)
                cred.RefreshToken = rtEl.GetString();

            if (TryProp(node, out var expEl, "expiresAt", "expires_at", "expiry", "expiresAtMs"))
                cred.ExpiresAt = ParseWhen(expEl);

            if (TryProp(node, out var subEl, "subscriptionType", "subscription_type", "plan", "tier")
                && subEl.ValueKind == JsonValueKind.String)
                cred.SubscriptionType = subEl.GetString();

            return cred;
        }
        catch
        {
            return null;
        }
    }

    private async Task<ClaudeCredentials?> RefreshAsync(ClaudeCredentials file, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(file.RefreshToken)) return null;
        try
        {
            var payload = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = file.RefreshToken!,
                ["client_id"] = ClientId
            });

            using var req = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            req.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");

            using var res = await Http.SendAsync(req, ct).ConfigureAwait(false);
            var body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!TryProp(root, out var atEl, "access_token", "accessToken")) return null;
            var access = atEl.GetString();
            if (string.IsNullOrWhiteSpace(access)) return null;

            var cred = new ClaudeCredentials
            {
                AccessToken = access!,
                RefreshToken = TryProp(root, out var rt, "refresh_token", "refreshToken") && rt.ValueKind == JsonValueKind.String
                    ? rt.GetString()
                    : file.RefreshToken,
                SubscriptionType = file.SubscriptionType,
                Source = "Claude Code (token renovado)"
            };

            if (TryProp(root, out var exp, "expires_in", "expiresIn")
                && exp.ValueKind == JsonValueKind.Number && exp.TryGetDouble(out var secs))
                cred.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(secs);
            else
                cred.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(50);

            WriteTokenCache(cred, file.RefreshToken);
            return cred;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteTokenCache(ClaudeCredentials cred, string? sourceRefreshToken)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.DataDir);
            var obj = new Dictionary<string, object?>
            {
                ["accessToken"] = cred.AccessToken,
                ["refreshToken"] = cred.RefreshToken,
                ["expiresAt"] = cred.ExpiresAt?.ToUnixTimeMilliseconds(),
                ["sourceHash"] = Fingerprint(sourceRefreshToken)
            };
            File.WriteAllText(TokenCachePath, JsonSerializer.Serialize(obj));
        }
        catch
        {
            // cache é opcional
        }
    }

    private static ClaudeCredentials? ReadTokenCache(string? sourceRefreshToken)
    {
        try
        {
            if (!File.Exists(TokenCachePath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(TokenCachePath));
            var root = doc.RootElement;

            if (TryProp(root, out var hash, "sourceHash") && hash.ValueKind == JsonValueKind.String)
            {
                if (hash.GetString() != Fingerprint(sourceRefreshToken)) return null; // Claude Code trocou de conta
            }

            if (!TryProp(root, out var at, "accessToken") || at.ValueKind != JsonValueKind.String) return null;
            var cred = new ClaudeCredentials
            {
                AccessToken = at.GetString() ?? "",
                Source = "Claude Code (token em cache)"
            };
            if (TryProp(root, out var rt, "refreshToken") && rt.ValueKind == JsonValueKind.String)
                cred.RefreshToken = rt.GetString();
            if (TryProp(root, out var exp, "expiresAt"))
                cred.ExpiresAt = ParseWhen(exp);
            return string.IsNullOrEmpty(cred.AccessToken) ? null : cred;
        }
        catch
        {
            return null;
        }
    }

    private static string Fingerprint(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "none";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes, 0, 8);
    }

    internal static bool TryProp(JsonElement el, out JsonElement value, params string[] names)
    {
        value = default;
        if (el.ValueKind != JsonValueKind.Object) return false;
        foreach (var p in el.EnumerateObject())
        {
            foreach (var n in names)
            {
                if (string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase))
                {
                    value = p.Value;
                    return true;
                }
            }
        }
        return false;
    }

    internal static DateTimeOffset? ParseWhen(JsonElement el)
    {
        try
        {
            if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var num))
            {
                if (num <= 0) return null;
                // segundos ou milissegundos desde a época
                return num > 100000000000d
                    ? DateTimeOffset.FromUnixTimeMilliseconds((long)num)
                    : DateTimeOffset.FromUnixTimeSeconds((long)num);
            }
            if (el.ValueKind == JsonValueKind.String)
            {
                var s = el.GetString();
                if (string.IsNullOrWhiteSpace(s)) return null;
                if (DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AdjustToUniversal, out var dto))
                    return dto;
                if (double.TryParse(s, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var n2))
                {
                    return n2 > 100000000000d
                        ? DateTimeOffset.FromUnixTimeMilliseconds((long)n2)
                        : DateTimeOffset.FromUnixTimeSeconds((long)n2);
                }
            }
        }
        catch
        {
            // formato desconhecido
        }
        return null;
    }
}
