using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeIndicator.Core;

/// <summary>Versão publicada no GitHub, quando é mais nova que a instalada.</summary>
public sealed class UpdateInfo
{
    public string Version = "";
    public string Tag = "";
    public string Notes = "";
    public string DownloadUrl = "";
    public string PageUrl = "";
    public long SizeBytes;
}

/// <summary>
/// Consulta as releases do repositório e, se houver versão nova, baixa o instalador e roda em
/// modo silencioso. O instalador já sabe se atualizar no lugar (fecha o app, reaproveita pasta e
/// opções), então a atualização pelo app é o mesmo caminho, sem as telas.
/// </summary>
public sealed class UpdateChecker
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        // a API do GitHub exige User-Agent identificável
        c.DefaultRequestHeaders.UserAgent.ParseAdd("ClaudeIndicator/" + AppInfo.Version);
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    /// <summary>Última verificação feita, para não consultar a cada abertura de tela.</summary>
    public DateTimeOffset LastCheck { get; private set; } = DateTimeOffset.MinValue;

    public UpdateInfo? Available { get; private set; }

    /// <summary>
    /// Procura versão nova. Sem <paramref name="force"/>, respeita o intervalo mínimo entre
    /// consultas — a API pública do GitHub permite 60 chamadas por hora e não queremos gastá-las.
    /// </summary>
    public async Task<UpdateInfo?> CheckAsync(AppSettings settings, bool force = false, CancellationToken ct = default)
    {
        if (!force)
        {
            if (!settings.CheckUpdates) return null;
            if (DateTimeOffset.Now - LastCheck < TimeSpan.FromHours(6)) return Available;
        }

        var repo = (settings.UpdateRepository ?? "").Trim();
        if (repo.Length == 0) return null;

        try
        {
            var url = $"https://api.github.com/repos/{repo}/releases/latest";
            using var res = await Http.GetAsync(url, ct).ConfigureAwait(false);
            LastCheck = DateTimeOffset.Now;
            if (!res.IsSuccessStatusCode) return null;

            var body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            var tag = Str(root, "tag_name");
            if (string.IsNullOrWhiteSpace(tag)) return null;

            var version = NormalizeVersion(tag);
            if (!IsNewer(version, AppInfo.Version)) { Available = null; return null; }

            var info = new UpdateInfo
            {
                Tag = tag,
                Version = version,
                Notes = Str(root, "body") ?? "",
                PageUrl = Str(root, "html_url") ?? $"https://github.com/{repo}/releases"
            };

            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    var name = Str(a, "name") ?? "";
                    if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                    info.DownloadUrl = Str(a, "browser_download_url") ?? "";
                    info.SizeBytes = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : 0;
                    break;
                }
            }

            Available = info;
            return info;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null; // sem rede, repositório privado, formato inesperado: silencioso de propósito
        }
    }

    /// <summary>Baixa o instalador para a pasta temporária, informando o progresso (0..1).</summary>
    public static async Task<string?> DownloadAsync(UpdateInfo info, IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(info.DownloadUrl)) return null;

        var dir = Path.Combine(Path.GetTempPath(), "ClaudeIndicatorUpdate");
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, $"ClaudeIndicator-Setup-{info.Version}.exe");

        try
        {
            using var res = await Http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return null;

            var total = res.Content.Headers.ContentLength ?? info.SizeBytes;
            using var stream = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var file = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true);

            var buffer = new byte[1 << 16];
            long read = 0;
            int n;
            while ((n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                read += n;
                if (total > 0) progress?.Report(Math.Clamp((double)read / total, 0, 1));
            }

            return target;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Roda o instalador em modo silencioso. Ele fecha o app, troca o executável e o inicia de
    /// novo — por isso este processo não precisa (nem consegue) fazer nada depois.
    /// A instalação em Program Files exige elevação: o Windows mostra o pedido de confirmação.
    /// </summary>
    public static bool RunInstaller(string setupPath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = setupPath,
                Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
                UseShellExecute = true
            };
            Process.Start(psi);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void OpenPage(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch
        {
            // sem navegador padrão: nada a fazer
        }
    }

    // ------------------------------------------------------------------

    private static string? Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>"v1.4.0" e "1.4.0" viram "1.4.0".</summary>
    public static string NormalizeVersion(string tag)
    {
        var t = tag.Trim();
        if (t.StartsWith("v", StringComparison.OrdinalIgnoreCase)) t = t.Substring(1);
        return t;
    }

    /// <summary>Compara versões numéricas campo a campo; texto desconhecido nunca é "mais novo".</summary>
    public static bool IsNewer(string candidate, string current)
    {
        if (!Version.TryParse(Pad(candidate), out var a)) return false;
        if (!Version.TryParse(Pad(current), out var b)) return false;
        return a > b;
    }

    private static string Pad(string v)
    {
        var core = v.Split('-', '+')[0];
        var parts = core.Split('.');
        return parts.Length switch
        {
            1 => core + ".0.0",
            2 => core + ".0",
            _ => core
        };
    }
}
