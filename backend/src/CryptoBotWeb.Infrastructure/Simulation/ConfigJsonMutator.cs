using System.Text.Json.Nodes;
using CryptoBotWeb.Core.DTOs;

namespace CryptoBotWeb.Infrastructure.Simulation;

/// <summary>
/// Applies swept parameter values onto a base ConfigJson and expands sweep specs into the full
/// list of combinations (cartesian product). Paths use dot/index notation into the config object:
/// "takeProfitPercent", "levels[0].entrySpreadPercent".
/// </summary>
public static class ConfigJsonMutator
{
    /// <summary>Hard cap on the cartesian product — protects against accidental million-run sweeps.</summary>
    public const int MaxCombinations = 3000;

    private const int MaxValuesPerParameter = 1000;

    public static string Apply(string baseConfigJson, IReadOnlyDictionary<string, decimal> overrides)
    {
        var root = JsonNode.Parse(baseConfigJson)
            ?? throw new ArgumentException("ConfigJson пуст — нечего оптимизировать.");
        foreach (var (path, value) in overrides)
            SetByPath(root, path, value);
        return root.ToJsonString();
    }

    /// <summary>
    /// Expands the specs into all combinations and fail-fasts on a path that doesn't resolve
    /// against the base config, so a typo surfaces as one readable error instead of N failed runs.
    /// </summary>
    public static List<Dictionary<string, decimal>> BuildCombinations(
        string baseConfigJson, List<OptimizationParameterSpec> parameters)
    {
        if (parameters.Count == 0)
            throw new ArgumentException("Не задано ни одного варьируемого параметра.");
        if (parameters.Select(p => p.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != parameters.Count)
            throw new ArgumentException("Пути варьируемых параметров должны быть уникальны.");

        var axes = parameters.Select(p => (p.Path, Values: ExpandValues(p))).ToList();

        long total = 1;
        foreach (var (_, values) in axes)
        {
            total *= values.Count;
            if (total > MaxCombinations)
                throw new ArgumentException(
                    $"Слишком много комбинаций (> {MaxCombinations}). Уменьшите диапазоны или увеличьте шаги.");
        }

        var probe = axes.ToDictionary(a => a.Path, a => a.Values[0]);
        Apply(baseConfigJson, probe);

        var combos = new List<Dictionary<string, decimal>>((int)total) { new() };
        foreach (var (path, values) in axes)
        {
            var next = new List<Dictionary<string, decimal>>(combos.Count * values.Count);
            foreach (var partial in combos)
                foreach (var v in values)
                    next.Add(new Dictionary<string, decimal>(partial) { [path] = v });
            combos = next;
        }
        return combos;
    }

    private static List<decimal> ExpandValues(OptimizationParameterSpec p)
    {
        if (string.IsNullOrWhiteSpace(p.Path))
            throw new ArgumentException("У варьируемого параметра не задан путь (path).");

        if (p.Values is { Count: > 0 })
        {
            var list = p.Values.Distinct().ToList();
            if (list.Count > MaxValuesPerParameter)
                throw new ArgumentException($"Параметр {p.Path}: слишком много значений (> {MaxValuesPerParameter}).");
            return list;
        }

        if (p.From is not { } from || p.To is not { } to || p.Step is not { } step)
            throw new ArgumentException($"Параметр {p.Path}: задайте либо список значений, либо от/до/шаг.");
        if (step <= 0)
            throw new ArgumentException($"Параметр {p.Path}: шаг должен быть > 0.");
        if (to < from)
            throw new ArgumentException($"Параметр {p.Path}: «до» меньше «от».");
        if ((to - from) / step + 1 > MaxValuesPerParameter)
            throw new ArgumentException($"Параметр {p.Path}: диапазон даёт больше {MaxValuesPerParameter} значений.");

        var values = new List<decimal>();
        for (var v = from; v <= to; v += step)
            values.Add(v);
        return values;
    }

    private static void SetByPath(JsonNode root, string path, decimal value)
    {
        var segments = ParsePath(path);
        JsonNode current = root;
        for (var i = 0; i < segments.Count; i++)
        {
            var (name, index) = segments[i];
            var last = i == segments.Count - 1;

            if (current is not JsonObject obj)
                throw new ArgumentException($"Путь '{path}': '{name}' — родительский узел не объект.");

            if (index == null)
            {
                if (last)
                {
                    // Require the field to already exist: a typo must fail loudly, not silently
                    // add a junk field and run every combination on an unchanged config.
                    if (!obj.ContainsKey(name))
                        throw new ArgumentException($"Путь '{path}': поле '{name}' не найдено в конфиге.");
                    obj[name] = MakeNumber(value);
                    return;
                }
                current = obj[name]
                    ?? throw new ArgumentException($"Путь '{path}': поле '{name}' не найдено в конфиге.");
            }
            else
            {
                if (obj[name] is not JsonArray arr)
                    throw new ArgumentException($"Путь '{path}': поле '{name}' не является массивом.");
                if (index.Value < 0 || index.Value >= arr.Count)
                    throw new ArgumentException(
                        $"Путь '{path}': индекс [{index}] вне массива '{name}' (длина {arr.Count}).");
                if (last)
                {
                    arr[index.Value] = MakeNumber(value);
                    return;
                }
                current = arr[index.Value]
                    ?? throw new ArgumentException($"Путь '{path}': '{name}[{index}]' пуст.");
            }
        }
    }

    // Whole values are written as integers: int-typed config fields (periods, counts, cycles)
    // reject "20.0" under System.Text.Json's strict number handling, while decimal fields read
    // an integer token just fine.
    private static JsonNode MakeNumber(decimal value) =>
        decimal.Truncate(value) == value && value >= long.MinValue && value <= long.MaxValue
            ? JsonValue.Create((long)value)
            : JsonValue.Create(value);

    private static List<(string Name, int? Index)> ParsePath(string path)
    {
        var result = new List<(string, int?)>();
        foreach (var raw in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var seg = raw.Trim();
            var bracket = seg.IndexOf('[');
            if (bracket < 0)
            {
                result.Add((seg, null));
                continue;
            }
            var close = seg.IndexOf(']', bracket);
            if (bracket == 0 || close != seg.Length - 1 ||
                !int.TryParse(seg[(bracket + 1)..close], out var idx))
                throw new ArgumentException($"Некорректный путь параметра: '{path}'.");
            result.Add((seg[..bracket], idx));
        }
        if (result.Count == 0)
            throw new ArgumentException("Пустой путь параметра.");
        return result;
    }
}
