using System.Text.Json;

namespace BuildAdvisor.Integration;

internal sealed record RouteRisk(string NextNode, decimal Probability);
internal sealed record SegmentRisk(decimal Probability, decimal Low, decimal High, string Confidence, IReadOnlyList<RouteRisk> Routes)
{
    public bool IsAvailable => Routes.Count > 0;
}

// Predicts the calibrated probability of death or at least 25% max-HP damage during the
// next three map points. The model is trained offline; runtime inference is deterministic.
internal static class SegmentRiskModel
{
    private sealed record Model(decimal Bias, IReadOnlyDictionary<string, decimal> Weights, decimal Slope, decimal Intercept, int TrainEvents, decimal TestEce);
    private static readonly Lazy<Model?> Data = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    public static SegmentRisk Assess(RunContext context)
    {
        var model = Data.Value;
        if (model is null) return new(0m, 0m, 0m, "модель недоступна", []);
        var routes = context.Route.Options.Count > 0
            ? context.Route.Options.Select(x => x.Next).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            : new[] { "unknown" };
        var predictions = routes.Select(next => new RouteRisk(next, Predict(model, context, next))).OrderBy(x => x.Probability).ToArray();
        var center = predictions.Average(x => x.Probability);
        var calibrationMargin = Math.Clamp(model.TestEce * 2m + (context.Route.IsKnown ? .03m : .08m), .04m, .18m);
        var low = Math.Max(0m, predictions.Min(x => x.Probability) - calibrationMargin);
        var high = Math.Min(1m, predictions.Max(x => x.Probability) + calibrationMargin);
        var confidence = !context.Route.IsKnown ? "маршрут не выбран" :
            model.TrainEvents >= 500_000 && high - low <= .20m ? "калиброванная" : "ограниченная";
        return new(center, low, high, confidence, predictions);
    }

    public static string Summary(SegmentRisk risk)
    {
        if (!risk.IsAvailable) return "Прогноз ближайших 3 узлов: модель недоступна.";
        var routes = risk.Routes.Count <= 1 ? "" : " Варианты: " + string.Join(", ", risk.Routes.Select(x => $"{x.NextNode} {(x.Probability * 100m):F0}%")) + ".";
        return $"Риск следующих 3 узлов: {(risk.Probability * 100m):F0}% (диапазон {(risk.Low * 100m):F0}–{(risk.High * 100m):F0}%, уверенность: {risk.Confidence}).{routes}";
    }

    private static decimal Predict(Model model, RunContext context, string next)
    {
        var hpRatio = context.MaxHp <= 0 ? 1m : (decimal)context.Hp / context.MaxHp;
        var character = NormalizeCharacter(context.Character); var act = Math.Clamp(context.Act, 1, 3);
        var hp = Bucket(hpRatio, .20m, .35m, .50m, .70m, .90m);
        var current = NormalizeNode(context.Route.Current); next = NormalizeNode(next);
        var features = new[]
        {
            $"character={character}", $"act={act}", $"act_floor={Math.Min(5, Math.Max(0, (context.Floor - 1) % 18 / 3))}",
            $"hp={hp}", $"deck={Bucket(context.Cards.Count, 12m, 18m, 24m, 30m, 36m)}",
            $"gold={Bucket(context.Gold, 49m, 99m, 149m, 249m)}", $"relics={Bucket(context.Relics.Count, 2m, 5m, 8m, 12m)}",
            $"ascension={Bucket(context.Ascension, 0m, 4m, 9m)}", $"current={current}", $"next={next}",
            $"character_act={character}:{act}", $"act_hp={act}:{hp}", $"next_hp={next}:{hp}", $"next_act={next}:{act}"
        };
        var raw = model.Bias + features.Sum(x => model.Weights.GetValueOrDefault(x));
        var calibrated = model.Slope * raw + model.Intercept;
        return (decimal)(1d / (1d + Math.Exp(-(double)Math.Clamp(calibrated, -40m, 40m))));
    }

    private static Model? Load()
    {
        try
        {
            var path = Path.Combine(Path.GetDirectoryName(typeof(SegmentRiskModel).Assembly.Location) ?? "", "segment-risk-v1.data");
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path)); var root = doc.RootElement;
            if (root.GetProperty("feature_contract").GetString() != "segment-danger-v1") return null;
            var weights = root.GetProperty("weights").EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetDecimal(), StringComparer.Ordinal);
            var calibration = root.GetProperty("calibration"); var test = root.GetProperty("test");
            return new(root.GetProperty("bias").GetDecimal(), weights, calibration.GetProperty("slope").GetDecimal(),
                calibration.GetProperty("intercept").GetDecimal(), root.GetProperty("train_events").GetInt32(), test.GetProperty("ece").GetDecimal());
        }
        catch (Exception error) { Console.WriteLine("Build Advisor segment model: " + error.Message); return null; }
    }

    private static int Bucket(decimal value, params decimal[] boundaries) => boundaries.Count(x => value > x);
    private static string NormalizeNode(string value) => string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim().ToLowerInvariant();
    private static string NormalizeCharacter(string value) => value.Replace("CHARACTER.", "", StringComparison.OrdinalIgnoreCase).ToLowerInvariant();
}
