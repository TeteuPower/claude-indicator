using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeIndicator.Core;

/// <summary>
/// Busca o consumo da assinatura no mesmo endpoint que o comando /usage do Claude Code usa.
/// O parser é tolerante: procura em qualquer lugar do JSON campos de porcentagem e de reset,
/// e associa cada um a uma barra por palavras-chave (configuráveis na aba Diagnóstico).
/// </summary>
public class UsageService
{
    private static readonly HttpClient Http = CreateClient();
    private readonly CredentialStore _store;

    public UsageService(CredentialStore store) => _store = store;

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        var c = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("ClaudeIndicator/1.0");
        return c;
    }

    public async Task<UsageSnapshot> FetchAsync(AppSettings settings, CancellationToken ct = default)
    {
        var snap = new UsageSnapshot { FetchedAt = DateTimeOffset.Now };

        ClaudeCredentials? cred;
        try
        {
            cred = await _store.GetAsync(settings, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            snap.Error = "Falha ao ler credenciais: " + ex.Message;
            return snap;
        }

        if (cred == null || string.IsNullOrWhiteSpace(cred.AccessToken))
        {
            snap.Error = settings.CredentialSource == "Manual"
                ? "Nenhum token informado. Abra Configurações › Conta."
                : "Credenciais do Claude Code não encontradas. Faça login com `claude` no terminal ou informe um token em Configurações › Conta.";
            return snap;
        }

        snap.Account = cred.Describe();
        var errors = new List<string>();

        foreach (var url in settings.EndpointList())
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cred!.AccessToken);
                    req.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
                    req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
                    req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                    using var res = await Http.SendAsync(req, ct).ConfigureAwait(false);
                    var body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                    if (res.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
                    {
                        var refreshed = await _store.ForceRefreshAsync(settings, ct).ConfigureAwait(false);
                        if (refreshed != null)
                        {
                            cred = refreshed;
                            continue; // tenta o mesmo endpoint com o token novo
                        }
                    }

                    if (!res.IsSuccessStatusCode)
                    {
                        errors.Add($"{Short(url)} → HTTP {(int)res.StatusCode}");
                        break;
                    }

                    snap.EndpointUsed = url;
                    snap.RawJson = Pretty(body);
                    snap.Bars = Map(body, settings);
                    if (snap.Bars.Count == 0)
                    {
                        snap.Error = "A resposta chegou, mas nenhuma barra foi reconhecida. Veja Configurações › Diagnóstico.";
                    }
                    return snap;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    errors.Add($"{Short(url)} → {ex.Message}");
                    break;
                }
            }
        }

        snap.Error = errors.Count > 0
            ? "Não foi possível obter o consumo: " + string.Join(" | ", errors)
            : "Não foi possível obter o consumo.";
        return snap;
    }

    // ------------------------------------------------------------------
    // Parser
    // ------------------------------------------------------------------

    private sealed class Candidate
    {
        public string Path = "";
        public double Percent;
        public DateTimeOffset? ResetsAt;
    }

    public static List<UsageBar> Map(string json, AppSettings settings)
    {
        var bars = new List<UsageBar>();
        var cands = new List<Candidate>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            Collect(doc.RootElement, "", cands);
        }
        catch
        {
            return bars;
        }
        if (cands.Count == 0) return bars;

        var fableKw = Keywords(settings.FableKeywords);

        var session = Pick(cands, Keywords(settings.SessionKeywords), fableKw);
        var weekly = Pick(cands, Keywords(settings.WeeklyKeywords), fableKw);
        var fable = Pick(cands, fableKw, Array.Empty<string>());

        if (session != null) bars.Add(ToBar(BarKind.Session, session));
        if (weekly != null) bars.Add(ToBar(BarKind.Weekly, weekly));
        if (fable != null) bars.Add(ToBar(BarKind.Fable, fable));

        // Nada casou por palavra-chave: usa a ordem em que apareceram no JSON.
        if (bars.Count == 0)
        {
            var kinds = new[] { BarKind.Session, BarKind.Weekly, BarKind.Fable };
            for (var i = 0; i < cands.Count && i < kinds.Length; i++)
                bars.Add(ToBar(kinds[i], cands[i]));
        }

        return bars;
    }

    private static UsageBar ToBar(BarKind kind, Candidate c) => new()
    {
        Kind = kind,
        Percent = Math.Clamp(c.Percent, 0, 100),
        ResetsAt = c.ResetsAt,
        SourcePath = c.Path
    };

    private static string[] Keywords(string csv)
    {
        var list = new List<string>();
        foreach (var part in (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var n = Norm(part);
            if (n.Length > 0) list.Add(n);
        }
        return list.ToArray();
    }

    private static Candidate? Pick(List<Candidate> cands, string[] keywords, string[] exclude)
    {
        if (keywords.Length == 0) return null;
        Candidate? best = null;
        var bestScore = int.MinValue;

        foreach (var c in cands)
        {
            var p = Norm(c.Path);
            var skip = false;
            foreach (var x in exclude)
            {
                if (x.Length > 0 && p.Contains(x, StringComparison.Ordinal)) { skip = true; break; }
            }
            if (skip) continue;

            var idx = -1;
            for (var i = 0; i < keywords.Length; i++)
            {
                if (p.Contains(keywords[i], StringComparison.Ordinal)) { idx = i; break; }
            }
            if (idx < 0) continue;

            // prioridade: palavra-chave mais à esquerda na lista, depois caminho mais curto
            var score = (keywords.Length - idx) * 1000 - p.Length;
            if (score > bestScore)
            {
                bestScore = score;
                best = c;
            }
        }
        return best;
    }

    private static void Collect(JsonElement el, string path, List<Candidate> list)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
            {
                double? pct = null;
                double? used = null;
                double? limit = null;
                DateTimeOffset? reset = null;
                var labelSuffix = "";

                foreach (var p in el.EnumerateObject())
                {
                    var name = Norm(p.Name);

                    if (p.Value.ValueKind == JsonValueKind.String && IsLabelKey(name))
                    {
                        var v = p.Value.GetString();
                        if (!string.IsNullOrWhiteSpace(v)) labelSuffix += "." + v;
                    }

                    if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetDouble(out var num))
                    {
                        if (pct == null && IsPercentKey(name)) pct = NormalizePercent(name, num);
                        else if (used == null && IsUsedKey(name)) used = num;
                        else if (limit == null && IsLimitKey(name)) limit = num;
                    }

                    if (reset == null && IsResetKey(name)) reset = CredentialStore.ParseWhen(p.Value);
                }

                if (pct == null && used.HasValue && limit.HasValue && limit.Value > 0)
                    pct = Math.Clamp(used.Value / limit.Value * 100.0, 0, 100);

                if (pct != null)
                {
                    // O que identifica a barra pode estar num objeto aninhado (ex.: o limite semanal
                    // por modelo vem como scope.model.display_name = "Fable"), então o rótulo do
                    // candidato também recolhe os nomes que estão logo abaixo dele.
                    var label = labelSuffix + NestedLabels(el, 3);
                    list.Add(new Candidate { Path = path + label, Percent = pct.Value, ResetsAt = reset });
                }

                foreach (var p in el.EnumerateObject())
                    Collect(p.Value, path + "." + p.Name + labelSuffix, list);
                break;
            }
            case JsonValueKind.Array:
            {
                var i = 0;
                foreach (var item in el.EnumerateArray())
                {
                    Collect(item, path + "[" + i + "]", list);
                    i++;
                }
                break;
            }
        }
    }

    /// <summary>
    /// Rótulos de objetos aninhados (até <paramref name="depth"/> níveis), usados para identificar
    /// a barra quando o nome do modelo não está no mesmo objeto da porcentagem.
    /// Só desce por objetos: arrays ficam de fora para não misturar itens irmãos.
    /// </summary>
    private static string NestedLabels(JsonElement el, int depth)
    {
        if (depth <= 0) return "";
        var sb = new StringBuilder();
        foreach (var p in el.EnumerateObject())
        {
            if (p.Value.ValueKind != JsonValueKind.Object) continue;

            foreach (var q in p.Value.EnumerateObject())
            {
                if (q.Value.ValueKind != JsonValueKind.String || !IsLabelKey(Norm(q.Name))) continue;
                var v = q.Value.GetString();
                if (!string.IsNullOrWhiteSpace(v)) sb.Append('.').Append(v);
            }

            sb.Append(NestedLabels(p.Value, depth - 1));
        }
        return sb.ToString();
    }

    private static bool IsLabelKey(string n) =>
        n is "type" or "name" or "id" or "key" or "window" or "period" or "limittype" or "limit" or "scope" or "kind" or "model"
            or "displayname" or "modelname" or "modelid" or "label";

    private static bool IsPercentKey(string n) =>
        n is "utilization" or "utilizationpercent" or "utilizationpct" or "percent" or "percentage"
            or "percentused" or "usedpercent" or "usagepercent" or "pctused" or "usedpct"
            or "fractionused" or "usedfraction" or "ratio" or "usedratio" or "consumed" or "consumedpercent";

    private static bool IsUsedKey(string n) =>
        n is "used" or "usedtokens" or "tokensused" or "count" or "requestsused" or "current";

    private static bool IsLimitKey(string n) =>
        n is "limit" or "total" or "max" or "quota" or "cap" or "allowance" or "totaltokens" or "tokenlimit";

    private static bool IsResetKey(string n) =>
        n is "resetsat" or "resetat" or "resets" or "reset" or "resetsatunix" or "resettime"
            or "windowresetsat" or "nextreset" or "expiresat" or "endsat" or "resetsatiso";

    private static double NormalizePercent(string key, double v)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) return 0;
        var isFraction = key.Contains("fraction", StringComparison.Ordinal) || key.Contains("ratio", StringComparison.Ordinal);
        if (isFraction || (v > 0 && v <= 1 && Math.Abs(v - Math.Floor(v)) > 1e-9))
            v *= 100.0;
        return Math.Clamp(v, 0, 100);
    }

    private static string Norm(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    private static string Short(string url)
    {
        try
        {
            var u = new Uri(url);
            return u.Host + u.AbsolutePath;
        }
        catch
        {
            return url;
        }
    }

    private static string Pretty(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return json;
        }
    }
}
