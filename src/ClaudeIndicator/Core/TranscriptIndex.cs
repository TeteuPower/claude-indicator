using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeIndicator.Core;

/// <summary>Tokens somados de um conjunto de turnos.</summary>
public sealed class TokenTotals
{
    public long Input { get; set; }
    public long Output { get; set; }
    public long CacheRead { get; set; }
    public long CacheWrite { get; set; }
    public long Turns { get; set; }

    public void Add(TokenTotals o)
    {
        Input += o.Input; Output += o.Output; CacheRead += o.CacheRead; CacheWrite += o.CacheWrite; Turns += o.Turns;
    }

    /// <summary>Custo aproximado, com os pesos configurados. Só faz sentido comparado a outro custo.</summary>
    public double Weighted(AppSettings s) =>
        Input + Output * s.WeightOutput + CacheWrite * s.WeightCacheWrite + CacheRead * s.WeightCacheRead;

    public long TotalTokens => Input + Output + CacheRead + CacheWrite;
}

/// <summary>Consumo de um projeto num período.</summary>
public sealed class ProjectUsage
{
    public string Path = "";
    public string Name = "";
    public TokenTotals All { get; } = new();
    public TokenTotals Fable { get; } = new();
    public int Prompts { get; set; }

    /// <summary>Fatia do consumo total do período (0..1), preenchida pelo agregador.</summary>
    public double Share { get; set; }
    public double FableShare { get; set; }
}

/// <summary>Um prompt digitado pelo usuário, com o custo do turno que ele disparou.</summary>
public sealed class PromptEntry
{
    public DateTimeOffset At { get; set; }
    public string File = "";
    public long Offset { get; set; }
    public string Project = "";
    public TokenTotals Cost { get; } = new();
    public double Share { get; set; }
}

/// <summary>
/// Índice do consumo por projeto lido das transcrições do Claude Code
/// (%USERPROFILE%\.claude\projects\**\*.jsonl).
///
/// Os arquivos só crescem no fim, então o índice guarda o offset já lido de cada um e, nas
/// próximas vezes, processa apenas o trecho novo. Uma varredura completa dos ~280 MB leva
/// alguns segundos; a incremental é imediata.
///
/// O texto dos prompts NÃO é copiado para o índice: guardamos arquivo + offset e a linha é
/// lida sob demanda quando a tela precisa mostrá-la.
/// </summary>
public sealed class TranscriptIndex
{
    private const int FormatVersion = 1;

    public static string ProjectsRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

    public static string CachePath => Path.Combine(AppSettings.DataDir, "transcripts-index.json");

    public static bool Available => Directory.Exists(ProjectsRoot);

    /// <summary>Progresso da varredura (mensagem curta para a barra de status).</summary>
    public event Action<string>? Progress;

    private readonly object _lock = new();
    private IndexData _data = new();
    private bool _loaded;

    // ------------------------------------------------------------------
    // Modelo persistido
    // ------------------------------------------------------------------

    private sealed class FileState
    {
        [JsonPropertyName("p")] public string Path { get; set; } = "";
        [JsonPropertyName("o")] public long Offset { get; set; }
        [JsonPropertyName("s")] public long Size { get; set; }
    }

    private sealed class Bucket
    {
        [JsonPropertyName("pj")] public int Project { get; set; }
        [JsonPropertyName("md")] public int Model { get; set; }
        [JsonPropertyName("h")] public long Hour { get; set; }   // horas desde a época, UTC
        [JsonPropertyName("i")] public long Input { get; set; }
        [JsonPropertyName("o")] public long Output { get; set; }
        [JsonPropertyName("cr")] public long CacheRead { get; set; }
        [JsonPropertyName("cw")] public long CacheWrite { get; set; }
        [JsonPropertyName("n")] public long Turns { get; set; }
    }

    private sealed class PromptRec
    {
        [JsonPropertyName("pj")] public int Project { get; set; }
        [JsonPropertyName("f")] public int File { get; set; }
        [JsonPropertyName("o")] public long Offset { get; set; }
        [JsonPropertyName("t")] public long Unix { get; set; }
        [JsonPropertyName("i")] public long Input { get; set; }
        [JsonPropertyName("ou")] public long Output { get; set; }
        [JsonPropertyName("cr")] public long CacheRead { get; set; }
        [JsonPropertyName("cw")] public long CacheWrite { get; set; }
        [JsonPropertyName("n")] public long Turns { get; set; }
    }

    private sealed class IndexData
    {
        [JsonPropertyName("v")] public int Version { get; set; } = FormatVersion;
        [JsonPropertyName("projects")] public List<string> Projects { get; set; } = new();
        [JsonPropertyName("models")] public List<string> Models { get; set; } = new();
        [JsonPropertyName("files")] public List<FileState> Files { get; set; } = new();
        [JsonPropertyName("buckets")] public List<Bucket> Buckets { get; set; } = new();
        [JsonPropertyName("prompts")] public List<PromptRec> Prompts { get; set; } = new();

        /// <summary>
        /// Hash dos uuid já contabilizados. Sessão retomada copia o histórico para o arquivo novo,
        /// então sem isto o mesmo turno seria somado mais de uma vez (medido: 48% a mais).
        /// </summary>
        [JsonPropertyName("seen")] public List<long> SeenUuids { get; set; } = new();

        [JsonIgnore] public DateTimeOffset ScannedAt { get; set; }
        [JsonPropertyName("scannedAt")] public string ScannedAtIso
        {
            get => ScannedAt.ToString("o");
            set => ScannedAt = DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var v) ? v : DateTimeOffset.MinValue;
        }
    }

    public DateTimeOffset ScannedAt
    {
        get { lock (_lock) return _data.ScannedAt; }
    }

    // ------------------------------------------------------------------
    // Varredura
    // ------------------------------------------------------------------

    /// <summary>Atualiza o índice: incremental quando possível, completo quando algo mudou fora do fim.</summary>
    public Task RefreshAsync(CancellationToken ct = default) => Task.Run(() => Refresh(ct), ct);

    private void Refresh(CancellationToken ct)
    {
        if (!Available) return;

        lock (_lock)
        {
            if (!_loaded)
            {
                _data = LoadCache() ?? new IndexData();
                _loaded = true;
            }

            var files = new List<string>(Directory.EnumerateFiles(ProjectsRoot, "*.jsonl", SearchOption.AllDirectories));
            files.Sort(StringComparer.OrdinalIgnoreCase);

            if (NeedsFullRebuild(files))
            {
                Progress?.Invoke("Lendo as transcrições pela primeira vez…");
                _data = new IndexData();
            }

            var known = new Dictionary<string, FileState>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in _data.Files) known[f.Path] = f;

            var projectIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < _data.Projects.Count; i++) projectIds[_data.Projects[i]] = i;
            var modelIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < _data.Models.Count; i++) modelIds[_data.Models[i]] = i;

            var buckets = new Dictionary<(int, int, long), Bucket>();
            foreach (var b in _data.Buckets) buckets[(b.Project, b.Model, b.Hour)] = b;

            var fileIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < _data.Files.Count; i++) fileIds[_data.Files[i].Path] = i;

            var seen = new HashSet<long>(_data.SeenUuids);
            var done = 0;
            var scannedAny = false;

            foreach (var path in files)
            {
                ct.ThrowIfCancellationRequested();
                done++;

                long size;
                try { size = new FileInfo(path).Length; }
                catch { continue; }

                if (!known.TryGetValue(path, out var state))
                {
                    state = new FileState { Path = path };
                    _data.Files.Add(state);
                    fileIds[path] = _data.Files.Count - 1;
                    known[path] = state;
                }

                if (state.Offset >= size) { state.Size = size; continue; } // nada novo

                if (done % 20 == 0)
                    Progress?.Invoke($"Lendo transcrições… {done}/{files.Count}");

                var fileId = fileIds[path];
                var newOffset = ScanFile(path, state.Offset, fileId, projectIds, modelIds, buckets, seen, ct);
                state.Offset = newOffset;
                state.Size = size;
                scannedAny = true;
            }

            if (scannedAny || _data.ScannedAt == DateTimeOffset.MinValue)
            {
                _data.Projects.Clear();
                foreach (var kv in Sorted(projectIds)) _data.Projects.Add(kv);
                _data.Models.Clear();
                foreach (var kv in Sorted(modelIds)) _data.Models.Add(kv);

                _data.Buckets = new List<Bucket>(buckets.Values);
                _data.SeenUuids = new List<long>(seen);
                _data.ScannedAt = DateTimeOffset.Now;
                SaveCache(_data);
            }

            Progress?.Invoke("");
        }
    }

    private static IEnumerable<string> Sorted(Dictionary<string, int> ids)
    {
        var arr = new string[ids.Count];
        foreach (var kv in ids) arr[kv.Value] = kv.Key;
        return arr;
    }

    /// <summary>Arquivo encolheu, sumiu ou o formato mudou: o índice inteiro é refeito.</summary>
    private bool NeedsFullRebuild(List<string> current)
    {
        if (_data.Version != FormatVersion) return true;
        if (_data.Files.Count == 0) return false;

        var set = new HashSet<string>(current, StringComparer.OrdinalIgnoreCase);
        foreach (var f in _data.Files)
        {
            if (!set.Contains(f.Path)) return true;
            try
            {
                if (new FileInfo(f.Path).Length < f.Offset) return true;
            }
            catch
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Um turno cobrado: o maior usage entre os registros que compartilham o requestId.</summary>
    private sealed class Group
    {
        public string? RequestId;
        public int Project, Model;
        public long Hour;
        public long Input, Output, CacheRead, CacheWrite;
        public long Total => Input + Output + CacheRead + CacheWrite;
    }

    private long ScanFile(string path, long startOffset, int fileId,
        Dictionary<string, int> projectIds, Dictionary<string, int> modelIds,
        Dictionary<(int, int, long), Bucket> buckets, HashSet<long> seen, CancellationToken ct)
    {
        // projeto padrão quando a linha não traz cwd: nome da pasta de topo do projeto
        var fallbackProject = FallbackProject(path);

        PromptRec? openPrompt = null;
        Group? pending = null;
        var lineStart = startOffset;

        void Commit()
        {
            if (pending == null || pending.Total == 0) { pending = null; return; }

            var key = (pending.Project, pending.Model, pending.Hour);
            if (!buckets.TryGetValue(key, out var b))
            {
                b = new Bucket { Project = pending.Project, Model = pending.Model, Hour = pending.Hour };
                buckets[key] = b;
            }
            b.Input += pending.Input; b.Output += pending.Output;
            b.CacheRead += pending.CacheRead; b.CacheWrite += pending.CacheWrite; b.Turns++;

            // o custo entra no último prompt digitado deste arquivo (inclui os subagentes do turno)
            if (openPrompt != null)
            {
                openPrompt.Input += pending.Input; openPrompt.Output += pending.Output;
                openPrompt.CacheRead += pending.CacheRead; openPrompt.CacheWrite += pending.CacheWrite;
                openPrompt.Turns++;
            }
            pending = null;
        }

        try
        {
            foreach (var (offset, line, next) in ReadLines(path, startOffset, ct))
            {
                lineStart = next;
                if (line.Length < 20) continue;

                // pré-filtro barato: só as linhas candidatas viram JSON
                var maybeAssistant = line.Contains("\"usage\"", StringComparison.Ordinal);
                var maybeUser = line.Contains("\"type\":\"user\"", StringComparison.Ordinal);
                if (!maybeAssistant && !maybeUser) continue;

                JsonDocument doc;
                try { doc = JsonDocument.Parse(line); }
                catch { continue; }

                using (doc)
                {
                    var root = doc.RootElement;

                    // o tipo tem que ser lido do campo, não do texto: registros last-prompt
                    // embutem a mensagem do usuário e cairiam no filtro por substring
                    if (!root.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String)
                        continue;
                    var type = typeEl.GetString();
                    if (type != "assistant" && type != "user") continue;

                    // histórico copiado por sessão retomada: o mesmo uuid não conta duas vezes
                    var uuid = root.TryGetProperty("uuid", out var uEl) && uEl.ValueKind == JsonValueKind.String
                        ? uEl.GetString()
                        : null;
                    if (uuid != null && !seen.Add(Hash(uuid))) continue;

                    var project = root.TryGetProperty("cwd", out var cwdEl) && cwdEl.ValueKind == JsonValueKind.String
                        ? (cwdEl.GetString() ?? fallbackProject)
                        : fallbackProject;
                    var pid = Id(projectIds, project);
                    var at = ReadTimestamp(root);

                    if (type == "user")
                    {
                        if (!IsTypedPrompt(root)) continue;
                        Commit(); // fecha o turno anterior antes de trocar de prompt
                        openPrompt = new PromptRec
                        {
                            Project = pid,
                            File = fileId,
                            Offset = offset,
                            Unix = at == default ? 0 : at.ToUnixTimeSeconds()
                        };
                        _data.Prompts.Add(openPrompt);
                        continue;
                    }

                    if (!root.TryGetProperty("message", out var msg)) continue;
                    if (!msg.TryGetProperty("usage", out var usage)) continue;

                    var model = msg.TryGetProperty("model", out var mEl) && mEl.ValueKind == JsonValueKind.String
                        ? mEl.GetString() ?? "?"
                        : "?";
                    if (model == "<synthetic>") continue; // resposta local, não consome nada

                    var input = Num(usage, "input_tokens");
                    var output = Num(usage, "output_tokens");
                    var cacheRead = Num(usage, "cache_read_input_tokens");
                    var cacheWrite = Num(usage, "cache_creation_input_tokens");
                    if (input + output + cacheRead + cacheWrite == 0) continue;

                    // Vários registros do mesmo request repetem o usage (medido: 5.140 de 6.191
                    // grupos com valores idênticos). Vale um por request, o maior — que é o
                    // acumulado final quando os valores crescem ao longo do streaming.
                    var reqId = root.TryGetProperty("requestId", out var rEl) && rEl.ValueKind == JsonValueKind.String
                        ? rEl.GetString()
                        : null;

                    if (pending != null && (reqId == null || pending.RequestId != reqId)) Commit();

                    var total = input + output + cacheRead + cacheWrite;
                    if (pending == null)
                    {
                        pending = new Group
                        {
                            RequestId = reqId,
                            Project = pid,
                            Model = Id(modelIds, model),
                            Hour = at == default ? 0 : at.ToUnixTimeSeconds() / 3600,
                            Input = input, Output = output, CacheRead = cacheRead, CacheWrite = cacheWrite
                        };
                    }
                    else if (total > pending.Total)
                    {
                        pending.Input = input; pending.Output = output;
                        pending.CacheRead = cacheRead; pending.CacheWrite = cacheWrite;
                    }

                    if (reqId == null) Commit(); // sem request para agrupar: fecha na hora
                }
            }

            Commit(); // último turno do arquivo
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // arquivo em uso/ilegível: retoma de onde parou na próxima vez
        }

        return lineStart;
    }

    /// <summary>
    /// Mensagens que aparecem como "user" mas não foram digitadas: saída de comando, avisos do
    /// sistema, notificações de tarefa. Só a lista explícita é descartada, para não engolir um
    /// prompt de verdade que por acaso comece com "&lt;".
    /// </summary>
    private static readonly string[] SyntheticPrefixes =
    {
        "<local-command-caveat>", "<local-command-stdout>", "<local-command-stderr>",
        "<command-name>", "<command-message>", "<command-args>",
        "<task-notification>", "<system-reminder>", "<user-prompt-submit-hook>",
        "<bash-input>", "<bash-stdout>", "<bash-stderr>"
    };

    /// <summary>
    /// Linha do usuário que é mesmo um prompt digitado: o conteúdo é texto e não é injeção do
    /// sistema. Resultado de ferramenta (tool_result), a maioria das linhas type=user, fica de fora.
    /// </summary>
    private static bool IsTypedPrompt(JsonElement root)
    {
        if (root.TryGetProperty("isSidechain", out var sc) && sc.ValueKind == JsonValueKind.True) return false;
        if (!root.TryGetProperty("message", out var msg)) return false;
        if (!msg.TryGetProperty("content", out var content)) return false;

        string? text = null;
        if (content.ValueKind == JsonValueKind.String)
        {
            text = content.GetString();
        }
        else if (content.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in content.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("type", out var t) || t.ValueKind != JsonValueKind.String) continue;
                if (t.GetString() != "text") return false; // tool_result, imagem etc.
                text = item.TryGetProperty("text", out var txt) && txt.ValueKind == JsonValueKind.String
                    ? txt.GetString()
                    : null;
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(text)) return false;

        var trimmed = text!.TrimStart();
        foreach (var prefix in SyntheticPrefixes)
        {
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    /// <summary>FNV-1a de 64 bits: guardar o hash do uuid custa 8 bytes em vez de 36.</summary>
    private static long Hash(string s)
    {
        unchecked
        {
            var h = 14695981039346656037UL;
            foreach (var c in s)
            {
                h ^= c;
                h *= 1099511628211UL;
            }
            return (long)h;
        }
    }

    private static DateTimeOffset ReadTimestamp(JsonElement root)
    {
        if (root.TryGetProperty("timestamp", out var ts) && ts.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(ts.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var at))
            return at;
        return default;
    }

    private static string FallbackProject(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath) ?? "";
        var root = ProjectsRoot;
        if (dir.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            var rest = dir.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var slash = rest.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar });
            if (slash > 0) rest = rest.Substring(0, slash);
            if (rest.Length > 0) return rest;
        }
        return dir;
    }

    private static int Id(Dictionary<string, int> ids, string value)
    {
        if (ids.TryGetValue(value, out var id)) return id;
        id = ids.Count;
        ids[value] = id;
        return id;
    }

    private static long Num(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

    // ------------------------------------------------------------------
    // Leitura das linhas com offset em bytes
    // ------------------------------------------------------------------

    /// <summary>
    /// Percorre as linhas completas a partir de um offset, devolvendo o offset de cada uma e o
    /// da próxima. A última linha sem '\n' é ignorada de propósito: o Claude Code pode estar
    /// escrevendo nela agora, e o offset devolvido faz a próxima varredura recomeçar dali.
    /// </summary>
    private static IEnumerable<(long Offset, string Line, long Next)> ReadLines(
        string path, long startOffset, CancellationToken ct)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
        if (startOffset > 0)
        {
            if (startOffset > fs.Length) yield break;
            fs.Position = startOffset;
        }

        var buffer = new byte[1 << 16];
        var line = new List<byte>(8192);
        var lineStart = fs.Position;
        var pos = fs.Position;

        int read;
        while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            for (var i = 0; i < read; i++)
            {
                var b = buffer[i];
                pos++;
                if (b != (byte)'\n')
                {
                    line.Add(b);
                    continue;
                }

                var len = line.Count;
                if (len > 0 && line[len - 1] == (byte)'\r') len--;
                if (len > 0)
                {
                    var text = Encoding.UTF8.GetString(line.ToArray(), 0, len);
                    yield return (lineStart, text, pos);
                }
                line.Clear();
                lineStart = pos;
            }
        }
    }

    /// <summary>Lê o texto de um prompt sob demanda. Nada disso é copiado para o índice.</summary>
    public static string ReadPromptText(string path, long offset, int maxChars = 4000)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (offset >= fs.Length) return "";
            fs.Position = offset;

            using var sr = new StreamReader(fs, Encoding.UTF8);
            var line = sr.ReadLine();
            if (string.IsNullOrEmpty(line)) return "";

            using var doc = JsonDocument.Parse(line);
            if (!doc.RootElement.TryGetProperty("message", out var msg)) return "";
            if (!msg.TryGetProperty("content", out var content)) return "";

            string text;
            if (content.ValueKind == JsonValueKind.String)
            {
                text = content.GetString() ?? "";
            }
            else
            {
                var sb = new StringBuilder();
                foreach (var item in content.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    if (item.TryGetProperty("type", out var t) && t.GetString() == "text" &&
                        item.TryGetProperty("text", out var txt) && txt.ValueKind == JsonValueKind.String)
                        sb.Append(txt.GetString());
                }
                text = sb.ToString();
            }

            text = text.Trim();
            return text.Length > maxChars ? text.Substring(0, maxChars) + "…" : text;
        }
        catch
        {
            return "";
        }
    }

    // ------------------------------------------------------------------
    // Consultas
    // ------------------------------------------------------------------

    /// <summary>Consumo por projeto no período, ordenado do maior para o menor.</summary>
    public List<ProjectUsage> Aggregate(DateTimeOffset from, DateTimeOffset to, AppSettings settings)
    {
        var result = new List<ProjectUsage>();
        lock (_lock)
        {
            var fableIds = FableModelSet(settings);
            var fromHour = from.ToUnixTimeSeconds() / 3600;
            var toHour = to.ToUnixTimeSeconds() / 3600;

            var byProject = new Dictionary<int, ProjectUsage>();
            foreach (var b in _data.Buckets)
            {
                if (b.Hour < fromHour || b.Hour > toHour) continue;
                if (b.Project < 0 || b.Project >= _data.Projects.Count) continue;

                if (!byProject.TryGetValue(b.Project, out var pu))
                {
                    var path = _data.Projects[b.Project];
                    pu = new ProjectUsage { Path = path, Name = FriendlyName(path) };
                    byProject[b.Project] = pu;
                }

                var t = new TokenTotals
                {
                    Input = b.Input, Output = b.Output, CacheRead = b.CacheRead,
                    CacheWrite = b.CacheWrite, Turns = b.Turns
                };
                pu.All.Add(t);
                if (fableIds.Contains(b.Model)) pu.Fable.Add(t);
            }

            var fromUnix = from.ToUnixTimeSeconds();
            var toUnix = to.ToUnixTimeSeconds();
            foreach (var p in _data.Prompts)
            {
                if (p.Unix < fromUnix || p.Unix > toUnix) continue;
                if (byProject.TryGetValue(p.Project, out var pu)) pu.Prompts++;
            }

            double total = 0, totalFable = 0;
            foreach (var pu in byProject.Values)
            {
                total += pu.All.Weighted(settings);
                totalFable += pu.Fable.Weighted(settings);
            }

            foreach (var pu in byProject.Values)
            {
                pu.Share = total > 0 ? pu.All.Weighted(settings) / total : 0;
                pu.FableShare = totalFable > 0 ? pu.Fable.Weighted(settings) / totalFable : 0;
                result.Add(pu);
            }

            Disambiguate(result);
            result.Sort((a, b) => b.All.Weighted(settings).CompareTo(a.All.Weighted(settings)));
        }
        return result;
    }

    /// <summary>Prompts de um projeto no período, do mais recente para o mais antigo.</summary>
    public List<PromptEntry> PromptsFor(string projectPath, DateTimeOffset from, DateTimeOffset to, AppSettings settings)
    {
        var list = new List<PromptEntry>();
        lock (_lock)
        {
            var pid = _data.Projects.FindIndex(p => string.Equals(p, projectPath, StringComparison.OrdinalIgnoreCase));
            if (pid < 0) return list;

            var fromUnix = from.ToUnixTimeSeconds();
            var toUnix = to.ToUnixTimeSeconds();
            double total = 0;

            foreach (var p in _data.Prompts)
            {
                if (p.Project != pid || p.Unix < fromUnix || p.Unix > toUnix) continue;
                if (p.File < 0 || p.File >= _data.Files.Count) continue;

                var e = new PromptEntry
                {
                    At = DateTimeOffset.FromUnixTimeSeconds(p.Unix),
                    File = _data.Files[p.File].Path,
                    Offset = p.Offset,
                    Project = projectPath
                };
                e.Cost.Input = p.Input; e.Cost.Output = p.Output;
                e.Cost.CacheRead = p.CacheRead; e.Cost.CacheWrite = p.CacheWrite; e.Cost.Turns = p.Turns;
                total += e.Cost.Weighted(settings);
                list.Add(e);
            }

            foreach (var e in list) e.Share = total > 0 ? e.Cost.Weighted(settings) / total : 0;
            list.Sort((a, b) => b.At.CompareTo(a.At));
        }
        return list;
    }

    private HashSet<int> FableModelSet(AppSettings settings)
    {
        var ids = new HashSet<int>();
        var needles = (settings.FableModelIds ?? "fable")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        for (var i = 0; i < _data.Models.Count; i++)
        {
            foreach (var n in needles)
            {
                if (n.Length > 0 && _data.Models[i].Contains(n, StringComparison.OrdinalIgnoreCase))
                {
                    ids.Add(i);
                    break;
                }
            }
        }
        return ids;
    }

    /// <summary>
    /// Vários caminhos terminam na mesma pasta ("…\repositorios\vscode"), o que faria projetos
    /// diferentes aparecerem com o mesmo nome. Quem colide ganha mais um nível de caminho, até
    /// ficar único.
    /// </summary>
    private static void Disambiguate(List<ProjectUsage> projects)
    {
        var depth = new int[projects.Count];
        for (var i = 0; i < projects.Count; i++)
        {
            depth[i] = 1;
            projects[i].Name = Tail(projects[i].Path, 1);
        }

        // só quem colide desce mais um nível, para os nomes não ficarem longos à toa
        for (var round = 0; round < 4; round++)
        {
            var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in projects)
                byName[p.Name] = 1 + (byName.TryGetValue(p.Name, out var c) ? c : 0);

            var changed = false;
            for (var i = 0; i < projects.Count; i++)
            {
                if (byName[projects[i].Name] <= 1) continue;
                depth[i]++;
                var next = Tail(projects[i].Path, depth[i]);
                if (next != projects[i].Name)
                {
                    projects[i].Name = next;
                    changed = true;
                }
            }
            if (!changed) return;
        }
    }

    /// <summary>Últimos <paramref name="segments"/> trechos do caminho.</summary>
    private static string Tail(string path, int segments)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parts = trimmed.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return path;

        var take = Math.Min(segments, parts.Length);
        return string.Join("\\", parts, parts.Length - take, take);
    }

    /// <summary>Nome curto do projeto: a última pasta do caminho.</summary>
    public static string FriendlyName(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var idx = trimmed.LastIndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar });
        var leaf = idx >= 0 && idx < trimmed.Length - 1 ? trimmed.Substring(idx + 1) : trimmed;
        return leaf.Length > 0 ? leaf : path;
    }

    // ------------------------------------------------------------------
    // Cache em disco
    // ------------------------------------------------------------------

    private static readonly JsonSerializerOptions CacheOpts = new() { WriteIndented = false };

    private static IndexData? LoadCache()
    {
        try
        {
            if (!File.Exists(CachePath)) return null;
            var data = JsonSerializer.Deserialize<IndexData>(File.ReadAllText(CachePath), CacheOpts);
            return data?.Version == FormatVersion ? data : null;
        }
        catch
        {
            return null; // cache corrompido: reconstrói
        }
    }

    private static void SaveCache(IndexData data)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.DataDir);
            var tmp = CachePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(data, CacheOpts));
            File.Move(tmp, CachePath, true); // troca atômica: nunca deixa o cache pela metade
        }
        catch
        {
            // sem permissão/espaço: o índice fica só em memória nesta execução
        }
    }
}
