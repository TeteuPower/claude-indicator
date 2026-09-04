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

/// <summary>
/// Login da assinatura feito dentro do app: abre o site do Claude, o usuário autoriza e cola o
/// código de volta na interface. É o mesmo fluxo OAuth (com PKCE) e o mesmo client_id público que o
/// `claude setup-token` usa, então quem não tem o Claude Code instalado consegue ver o consumo.
///
/// O redirect é a página oficial de código do console da Anthropic, que EXIBE o "code#state" para
/// colar — por isso o app não precisa subir servidor nem abrir porta para receber o callback.
///
/// O token fica só neste computador, em %APPDATA%\ClaudeIndicator\login.json.
/// </summary>
public static class ClaudeLogin
{
    // client_id público do Claude Code (o mesmo do `setup-token`)
    private const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    private const string AuthorizeUrl = "https://claude.com/cai/oauth/authorize";
    private const string TokenUrl = "https://platform.claude.com/v1/oauth/token";

    /// <summary>Página do console que mostra o código para o usuário copiar.</summary>
    private const string RedirectUri = "https://console.anthropic.com/oauth/code/callback";

    // user:profile é o que libera LER o consumo (sem ele o endpoint de uso responde 403
    // "scope requirement user:profile"); user:inference vem junto porque é o par que o
    // Claude Code pede — pedir um escopo diferente do dele arrisca o endpoint recusar o token.
    private const string Scope = "user:inference user:profile";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(25) };

    private static string FilePath => Path.Combine(AppSettings.DataDir, "login.json");

    /// <summary>Sessão de autorização aberta: guarda o PKCE até o usuário colar o código.</summary>
    private sealed class PendingLogin
    {
        public string Verifier = "";
        public string State = "";
        public DateTimeOffset StartedAt;
    }

    private static PendingLogin? _pending;
    private static readonly object Lock = new();

    public static bool Connected => File.Exists(FilePath);

    /// <summary>Existe uma autorização em andamento esperando o código (expira em 10 minutos).</summary>
    public static bool WaitingForCode
    {
        get
        {
            lock (Lock)
            {
                if (_pending == null) return false;
                if (DateTimeOffset.UtcNow - _pending.StartedAt > TimeSpan.FromMinutes(10))
                {
                    _pending = null;
                    return false;
                }
                return true;
            }
        }
    }

    /// <summary>Monta a URL de autorização e guarda o PKCE desta tentativa.</summary>
    public static string Start()
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        lock (Lock)
        {
            _pending = new PendingLogin
            {
                Verifier = verifier,
                State = state,
                StartedAt = DateTimeOffset.UtcNow
            };
        }

        return $"{AuthorizeUrl}?code=true&client_id={ClientId}&response_type=code"
               + $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}"
               + $"&scope={Uri.EscapeDataString(Scope)}"
               + $"&code_challenge={challenge}&code_challenge_method=S256&state={state}";
    }

    public static void CancelPending()
    {
        lock (Lock) _pending = null;
    }

    /// <summary>
    /// Troca pelo token o que o usuário colou. Aceita o "code#state" que a página do console mostra,
    /// a URL inteira do callback ou só o código.
    /// </summary>
    public static async Task<ClaudeCredentials> FinishAsync(string pasted, CancellationToken ct = default)
    {
        PendingLogin pending;
        lock (Lock)
        {
            if (_pending == null || DateTimeOffset.UtcNow - _pending.StartedAt > TimeSpan.FromMinutes(10))
            {
                _pending = null;
                throw new InvalidOperationException(
                    "A autorização expirou. Clique em \"Abrir o site do Claude\" de novo.");
            }
            pending = _pending;
        }

        var (code, state) = ParsePasted(pasted);
        if (code.Length == 0)
            throw new InvalidOperationException("Cole o código que apareceu no site do Claude.");
        if (state.Length > 0 && state != pending.State)
            throw new InvalidOperationException(
                "O código é de outra tentativa de login. Clique em \"Abrir o site do Claude\" de novo e use o código novo.");

        var payload = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["state"] = pending.State,
            ["client_id"] = ClientId,
            ["redirect_uri"] = RedirectUri,
            ["code_verifier"] = pending.Verifier
        });

        var cred = await PostTokenAsync(payload, null, ct).ConfigureAwait(false);
        lock (Lock) _pending = null; // código é de uso único
        Save(cred);
        return cred;
    }

    /// <summary>Credenciais guardadas por este login, ou null se ninguém entrou pelo app.</summary>
    public static ClaudeCredentials? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
            var root = doc.RootElement;

            if (!CredentialStore.TryProp(root, out var at, "accessToken") || at.ValueKind != JsonValueKind.String)
                return null;
            var token = at.GetString();
            if (string.IsNullOrWhiteSpace(token)) return null;

            var cred = new ClaudeCredentials
            {
                AccessToken = token!,
                Source = "Login no app"
            };
            if (CredentialStore.TryProp(root, out var rt, "refreshToken") && rt.ValueKind == JsonValueKind.String)
                cred.RefreshToken = rt.GetString();
            if (CredentialStore.TryProp(root, out var exp, "expiresAt"))
                cred.ExpiresAt = CredentialStore.ParseWhen(exp);
            if (CredentialStore.TryProp(root, out var sub, "subscriptionType") && sub.ValueKind == JsonValueKind.String)
                cred.SubscriptionType = sub.GetString();
            return cred;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>E-mail da conta que entrou, quando o Claude devolveu essa informação.</summary>
    public static string? Email()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
            if (CredentialStore.TryProp(doc.RootElement, out var el, "email") && el.ValueKind == JsonValueKind.String)
            {
                var v = el.GetString();
                return string.IsNullOrWhiteSpace(v) ? null : v;
            }
        }
        catch
        {
            // arquivo corrompido: some com o e-mail, o login continua valendo
        }
        return null;
    }

    /// <summary>Renova o token deste login pelo refresh_token. Devolve null se não deu.</summary>
    public static async Task<ClaudeCredentials?> RefreshAsync(CancellationToken ct = default)
    {
        var current = Load();
        if (string.IsNullOrWhiteSpace(current?.RefreshToken)) return null;

        try
        {
            var payload = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = current!.RefreshToken!,
                ["client_id"] = ClientId
            });

            var cred = await PostTokenAsync(payload, current, ct).ConfigureAwait(false);
            Save(cred);
            return cred;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Esquece o login (o botão "Sair desta conta").</summary>
    public static void Clear()
    {
        lock (Lock) _pending = null;
        _email = null;
        try
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);
        }
        catch
        {
            // arquivo travado: o token continua lá, mas o usuário pode trocar a fonte
        }
    }

    // ------------------------------------------------------------------

    private static async Task<ClaudeCredentials> PostTokenAsync(string payload, ClaudeCredentials? previous,
        CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");

        using var res = await Http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"O Claude recusou o código (HTTP {(int)res.StatusCode}). "
                                                + Trim(body));

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        if (!CredentialStore.TryProp(root, out var at, "access_token", "accessToken")
            || at.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("A resposta do Claude não trouxe o token de acesso.");

        var access = at.GetString();
        if (string.IsNullOrWhiteSpace(access))
            throw new InvalidOperationException("A resposta do Claude trouxe um token vazio.");

        var cred = new ClaudeCredentials
        {
            AccessToken = access!.Trim(),
            RefreshToken = CredentialStore.TryProp(root, out var rt, "refresh_token", "refreshToken")
                           && rt.ValueKind == JsonValueKind.String
                ? rt.GetString()
                : previous?.RefreshToken,
            SubscriptionType = ReadPlan(root) ?? previous?.SubscriptionType,
            Source = "Login no app"
        };

        cred.ExpiresAt = CredentialStore.TryProp(root, out var exp, "expires_in", "expiresIn")
                         && exp.ValueKind == JsonValueKind.Number && exp.TryGetDouble(out var secs)
            ? DateTimeOffset.UtcNow.AddSeconds(secs)
            : DateTimeOffset.UtcNow.AddMinutes(50);

        _email = ReadEmail(root) ?? _email;
        return cred;
    }

    private static string? _email;

    private static void Save(ClaudeCredentials cred)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.DataDir);
            var obj = new Dictionary<string, object?>
            {
                ["accessToken"] = cred.AccessToken,
                ["refreshToken"] = cred.RefreshToken,
                ["expiresAt"] = cred.ExpiresAt?.ToUnixTimeMilliseconds(),
                ["subscriptionType"] = cred.SubscriptionType,
                ["email"] = _email ?? Email(),
                ["connectedAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            File.WriteAllText(FilePath, JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Entrou, mas não deu para guardar o token em "
                                                + AppSettings.DataDir + ": " + ex.Message);
        }
    }

    /// <summary>
    /// Separa código e state do que foi colado. A página do console mostra os dois juntos como
    /// "code#state"; quem copia a barra de endereços traz a URL inteira.
    /// </summary>
    internal static (string Code, string State) ParsePasted(string pasted)
    {
        var s = (pasted ?? "").Trim();
        if (s.Length == 0) return ("", "");

        // URL inteira do callback
        var i = s.IndexOf("code=", StringComparison.OrdinalIgnoreCase);
        if (s.Contains("://", StringComparison.Ordinal) && i >= 0)
        {
            var code = CutParam(s, i + 5);
            var state = "";
            var j = s.IndexOf("state=", StringComparison.OrdinalIgnoreCase);
            if (j >= 0) state = CutParam(s, j + 6);
            return (Uri.UnescapeDataString(code), Uri.UnescapeDataString(state));
        }

        // "code#state" (o que a página do console exibe num campo só)
        var h = s.IndexOf('#');
        if (h > 0) return (s.Substring(0, h).Trim(), s.Substring(h + 1).Trim());

        return (s, "");
    }

    private static string CutParam(string s, int start)
    {
        var end = start;
        while (end < s.Length && s[end] != '&' && s[end] != '#'
               && !char.IsWhiteSpace(s[end])) end++;
        return s.Substring(start, end - start);
    }

    private static string? ReadPlan(JsonElement root)
    {
        if (CredentialStore.TryProp(root, out var direct, "subscriptionType", "subscription_type")
            && direct.ValueKind == JsonValueKind.String)
            return direct.GetString();

        foreach (var owner in new[] { "account", "organization" })
        {
            if (!CredentialStore.TryProp(root, out var node, owner)) continue;
            if (CredentialStore.TryProp(node, out var plan, "subscriptionType", "subscription_type",
                    "billing_type", "organization_type") && plan.ValueKind == JsonValueKind.String)
                return plan.GetString();
        }
        return null;
    }

    private static string? ReadEmail(JsonElement root)
    {
        if (CredentialStore.TryProp(root, out var direct, "email", "email_address")
            && direct.ValueKind == JsonValueKind.String)
            return direct.GetString();

        if (CredentialStore.TryProp(root, out var account, "account")
            && CredentialStore.TryProp(account, out var mail, "email_address", "email")
            && mail.ValueKind == JsonValueKind.String)
            return mail.GetString();

        return null;
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes)
        .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static string Trim(string body) => body.Length <= 200 ? body : body.Substring(0, 200) + "…";
}
