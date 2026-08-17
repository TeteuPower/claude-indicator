using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ClaudeIndicator.Core;

/// <summary>
/// Descobre para onde foi uma pasta de projeto que não existe mais.
///
/// O caminho vem do cwd gravado na transcrição, então projetos movidos ou renomeados aparecem com
/// o caminho antigo. A busca é por evidência, nunca por chute: procura pastas cujo caminho termine
/// igual ao antigo e desempata pelas subpastas que o projeto comprovadamente tinha (os próprios
/// subprojetos registrados no índice). Sem vencedor claro, devolve null e a interface diz apenas
/// que a pasta sumiu.
/// </summary>
public static class ProjectPathResolver
{
    /// <summary>Pastas que nunca contêm projeto e explodem o custo da varredura.</summary>
    private static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", "bin", "obj", ".vs", "dist", "build", "packages", "venv", ".venv",
        "target", "__pycache__", ".next", ".nuget", "AppData"
    };

    private const int MaxDepth = 4;

    private static readonly Dictionary<string, string?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Lock = new();

    /// <summary>
    /// Caminho atual do projeto, ou null se não dá para afirmar.
    /// <paramref name="hints"/> são nomes de subpastas que o projeto tinha.
    /// </summary>
    public static string? Resolve(string oldPath, IReadOnlyCollection<string> hints)
    {
        if (string.IsNullOrWhiteSpace(oldPath)) return null;

        lock (Lock)
        {
            if (Cache.TryGetValue(oldPath, out var cached)) return cached;
        }

        string? result = null;
        try
        {
            result = Search(oldPath, hints);
        }
        catch
        {
            // caminho inválido, disco removido, sem permissão: fica sem resposta mesmo
        }

        lock (Lock)
        {
            Cache[oldPath] = result;
        }
        return result;
    }

    public static void ClearCache()
    {
        lock (Lock) Cache.Clear();
    }

    // ------------------------------------------------------------------

    private static string? Search(string oldPath, IReadOnlyCollection<string> hints)
    {
        if (Directory.Exists(oldPath)) return null; // não sumiu

        var segments = oldPath.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2) return null;

        // ancestral mais profundo que ainda existe: é onde a busca começa
        var ancestorLength = -1;
        for (var i = segments.Length - 1; i >= 1; i--)
        {
            var candidate = string.Join("\\", segments, 0, i);
            if (candidate.EndsWith(":")) candidate += "\\";
            if (Directory.Exists(candidate)) { ancestorLength = i; break; }
        }
        if (ancestorLength <= 0) return null;

        var ancestor = string.Join("\\", segments, 0, ancestorLength);
        if (ancestor.EndsWith(":")) ancestor += "\\";

        // tenta preservar o máximo do caminho antigo: primeiro o rabo inteiro, depois mais curto
        for (var tailLength = segments.Length - ancestorLength; tailLength >= 1; tailLength--)
        {
            var tail = string.Join("\\", segments, segments.Length - tailLength, tailLength);
            var matches = FindEndingWith(ancestor, tail);
            if (matches.Count == 0) continue;

            var best = Pick(matches, hints);
            if (best != null) return best;
        }

        return null;
    }

    /// <summary>Pastas sob <paramref name="root"/> cujo caminho termina com <paramref name="tail"/>.</summary>
    private static List<string> FindEndingWith(string root, string tail)
    {
        var found = new List<string>();
        var suffix = "\\" + tail.TrimStart('\\');

        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((root, 0));

        while (queue.Count > 0 && found.Count < 40)
        {
            var (dir, depth) = queue.Dequeue();
            if (depth > MaxDepth) continue;

            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateDirectories(dir);
            }
            catch
            {
                continue;
            }

            foreach (var child in children)
            {
                var name = Path.GetFileName(child);
                if (name.Length == 0 || Skip.Contains(name) || name.StartsWith(".")) continue;

                if (child.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) found.Add(child);
                queue.Enqueue((child, depth + 1));
            }
        }
        return found;
    }

    /// <summary>
    /// Escolhe entre os candidatos pelas subpastas que o projeto tinha. Só devolve resposta com
    /// vencedor isolado — empate significa que não dá para saber, e chutar seria pior que nada.
    /// </summary>
    private static string? Pick(List<string> candidates, IReadOnlyCollection<string> hints)
    {
        if (candidates.Count == 1) return candidates[0];
        if (hints.Count == 0) return null; // ambíguo e sem evidência

        var scored = candidates
            .Select(c => (Path: c, Score: hints.Count(h => SafeExists(Path.Combine(c, h)))))
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Path.Length)
            .ToList();

        if (scored[0].Score == 0) return null;
        if (scored.Count > 1 && scored[1].Score == scored[0].Score) return null; // empate

        return scored[0].Path;
    }

    private static bool SafeExists(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch
        {
            return false;
        }
    }
}
