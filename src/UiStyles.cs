using System.Text.Json;
using Godot;

namespace BuildAdvisor.UI;

// 7.3: selectable interface styles. "Parchment" is the 6.24 look, unchanged; the other five follow
// the game's characters. A style is a palette, the shape of the panel and of the seal, and a small
// ornament in the panel header (UiOrnaments). Tier colours of the ribbons do not change with the
// style: S purple, A red, B amber, C blue, D grey must mean the same thing everywhere.
internal sealed record UiStyle(
    string Id, string Name,
    Color Paper, Color PaperDark, Color PaperLine, Color Ink, Color InkSoft, Color Accent, Color Gold,
    Color Field, Color Section, Color Hover, Color Pressed, Color Rule,
    Color SealFill, Color SealHover, Color SealPressed, Color SealInk, Color SealRim,
    int PanelRadius, int PanelBorder, int ControlRadius,
    // Corner radii of the 74 px seal: top-left, top-right, bottom-right, bottom-left.
    (int TopLeft, int TopRight, int BottomRight, int BottomLeft) SealRadii);

internal static class UiStyles
{
    private static Color C(string hex) => new(hex);

    internal static readonly UiStyle Parchment = new("parchment", "Стиль: Пергамент",
        C("e9dcc0"), C("ddcba5"), C("b9a27a"), C("3a2a1a"), C("6b5639"), C("8e2b22"), C("c9a24a"),
        C("f3ead6"), C("e2d2b1"), C("d4bf94"), C("c9b083"), C("b9a27a"),
        C("8e2b22"), C("a3352a"), C("6f2019"), C("f6e7c9"), C("c9a24a"),
        6, 2, 4, (37, 37, 37, 37));

    // Forged plate: dark iron, ember accent, square corners, rivets, a sword with sparks.
    internal static readonly UiStyle Ironclad = new("ironclad", "Стиль: Латоносец",
        C("2b2321"), C("3a2e2a"), C("705548"), C("f2e4d3"), C("c9b29b"), C("f0803c"), C("e9b04e"),
        C("221b19"), C("342a27"), C("4a3a34"), C("5a4238"), C("8f3a22"),
        C("b83a1f"), C("d14a26"), C("8f2a15"), C("ffeccd"), C("9a8c80"),
        2, 3, 2, (8, 8, 8, 8));

    // Night green and poison: a dagger that drips, a blade-shaped seal.
    internal static readonly UiStyle Silent = new("silent", "Стиль: Безмолвная",
        C("1c2721"), C("26362c"), C("4f6e5a"), C("e4eedb"), C("a8bda5"), C("9bd65a"), C("dcc56c"),
        C("16201b"), C("223028"), C("30443a"), C("3a5245"), C("4f6e5a"),
        C("2c5c38"), C("367044"), C("214a2b"), C("eaf7d2"), C("9bd65a"),
        10, 1, 8, (37, 8, 37, 8));

    // Steel blue and cyan: orb slots (lightning, frost, dark and an empty one), a circuit trace.
    internal static readonly UiStyle Defect = new("defect", "Стиль: Дефект",
        C("16222f"), C("1f3142"), C("3f6c8c"), C("dcebf6"), C("9db8cd"), C("62d6f2"), C("f4cf55"),
        C("101a25"), C("1b2a3a"), C("29425a"), C("32506c"), C("3f6c8c"),
        C("1d4a66"), C("255c7e"), C("163a50"), C("d9f6ff"), C("62d6f2"),
        0, 2, 0, (16, 16, 16, 16));

    // Royal indigo and gold: a crown and a small constellation that twinkles.
    internal static readonly UiStyle Regent = new("regent", "Стиль: Наследник",
        C("1f1b3d"), C("2c2756"), C("6c61ad"), C("f2edff"), C("bcb3e0"), C("f2c761"), C("ffdb80"),
        C("17142f"), C("28234c"), C("39326c"), C("453d82"), C("6c61ad"),
        C("3d3180"), C("4a3c98"), C("2f2566"), C("ffe9a8"), C("f2c761"),
        12, 2, 9, (37, 37, 37, 37));

    // Bone and ash with soul pink: a skull with glowing eyes, rising wisps, a bone seal.
    internal static readonly UiStyle Necrobinder = new("necrobinder", "Стиль: Призывающая",
        C("dcd8cf"), C("cbc6ba"), C("8f8879"), C("231d2c"), C("51485f"), C("7a2470"), C("c84fa6"),
        C("e9e6de"), C("d2cdc2"), C("c3bdb0"), C("b6afa1"), C("8f8879"),
        C("e6e1d6"), C("f3efe6"), C("cfc9bc"), C("5a1f6e"), C("5a1f6e"),
        3, 2, 3, (37, 37, 16, 16));

    internal static readonly IReadOnlyList<UiStyle> All = [Parchment, Ironclad, Silent, Defect, Regent, Necrobinder];

    // An unknown or missing id (a file from a newer version, a damaged file) is the standard look.
    internal static UiStyle Resolve(string? id) =>
        All.FirstOrDefault(x => string.Equals(x.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? Parchment;

    private sealed record Saved(string Style);
    private static readonly string SavePath = ModPaths.UserData("ui-style.json");
    private static UiStyle? current;

    // The choice persists between sessions. Regressions always see the standard style and never
    // read or write the user's file.
    internal static UiStyle Current
    {
        get => BuildAdvisor.Integration.BuildAdvisorRegression.IsRunning ? Parchment : current ??= Load();
        set
        {
            if (BuildAdvisor.Integration.BuildAdvisorRegression.IsRunning) return;
            current = value;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SavePath)!);
                File.WriteAllText(SavePath + ".tmp", JsonSerializer.Serialize(new Saved(value.Id)));
                File.Move(SavePath + ".tmp", SavePath, true);
            }
            catch { /* the style still applies to this session */ }
        }
    }

    private static UiStyle Load()
    {
        try { return File.Exists(SavePath) ? Resolve(JsonSerializer.Deserialize<Saved>(File.ReadAllText(SavePath))?.Style) : Parchment; }
        catch { return Parchment; }
    }
}

// One drawn element of an ornament. Kind: "poly" (filled polygon) or "line" (stroke of Width).
// Anim: "" (static), "pulse" (opacity breathes), "rise" (drifts up and fades), "fall" (drips down).
internal sealed record OrnamentShape(string Kind, Vector2[] Points, Color Color, float Width = 0f, string Anim = "", float Delay = 0f);

// Small character details in the panel header and on its rule, as plain geometry: the bundled fonts
// have no such glyphs, and geometry looks the same on every machine. Coordinates are in the panel's
// space; the header strip between the title and the close button is free (regression-checked).
internal static class UiOrnaments
{
    // Free header strip: right of the "Build Advisor" title, left of the close button, above the run line.
    internal const float HeaderLeft = 330f, HeaderRight = 572f, HeaderTop = 8f, HeaderBottom = 54f;

    internal static IReadOnlyList<OrnamentShape> For(UiStyle style, float panelWidth, float panelHeight, float ruleY)
    {
        var shapes = new List<OrnamentShape>();
        var left = 22f; var right = panelWidth - 22f; var middle = panelWidth / 2f;
        switch (style.Id)
        {
            case "ironclad":
            {
                var steel = new Color("cfc7bf");
                // A sword laid across the header, point to the left.
                shapes.Add(Poly(steel, (420, 32), (434, 27.5f), (518, 27.5f), (518, 36.5f), (434, 36.5f)));
                shapes.Add(new("line", [new(438, 32), new(512, 32)], new Color("8f8780"), 1.5f));
                shapes.Add(Poly(style.Gold, (518, 20), (524, 20), (524, 44), (518, 44)));
                shapes.Add(Poly(new Color("5a3a2a"), (524, 28.5f), (546, 28.5f), (546, 35.5f), (524, 35.5f)));
                shapes.Add(Circle(style.Gold, 551, 32, 5));
                // Sparks off the point.
                shapes.Add(Star(style.Accent, 407, 25, 3.4f) with { Anim = "pulse" });
                shapes.Add(Star(new Color("ffb066"), 397, 17, 2.4f) with { Anim = "pulse", Delay = .35f });
                shapes.Add(Star(new Color("e05a2a"), 402, 41, 2f) with { Anim = "pulse", Delay = .7f });
                // Rivets in the corners of the plate.
                foreach (var (x, y) in new[] { (11f, 11f), (panelWidth - 11f, 11f), (11f, panelHeight - 11f), (panelWidth - 11f, panelHeight - 11f) })
                {
                    shapes.Add(Circle(new Color("8a7a70"), x, y, 4.2f));
                    shapes.Add(Circle(new Color("cdbdb1"), x - 1.2f, y - 1.2f, 1.5f));
                }
                break;
            }
            case "silent":
            {
                // A thin dagger, point to the left, with poison running off the blade.
                shapes.Add(Poly(new Color("d5e2d0"), (440, 32), (458, 28.5f), (520, 29.5f), (520, 34.5f), (458, 35.5f)));
                shapes.Add(Poly(style.Gold, (520, 23), (524, 23), (524, 41), (520, 41)));
                shapes.Add(Poly(style.SealFill, (524, 29.5f), (545, 29.5f), (545, 34.5f), (524, 34.5f)));
                shapes.Add(Circle(style.Gold, 549, 32, 3.6f));
                shapes.Add(Drop(style.Accent, 442, 38, 3.4f) with { Anim = "fall" });
                shapes.Add(Drop(style.Accent, 452, 40, 2.4f) with { Anim = "fall", Delay = .8f });
                // Blade tips at both ends of the rule.
                shapes.Add(Star(style.Accent, left, ruleY, 5f));
                shapes.Add(Star(style.Accent, right, ruleY, 5f));
                break;
            }
            case "defect":
            {
                // Orb slots: lightning, frost, dark and one that is still empty.
                var fills = new Color?[] { new Color("f4cf55"), new Color("a8e4ff"), new Color("9a6bff"), null };
                for (var i = 0; i < fills.Length; i++)
                {
                    var x = 452f + i * 34f;
                    if (fills[i] is { } fill)
                    {
                        shapes.Add(Circle(fill, x, 32, 9f) with { Anim = "pulse", Delay = i * .4f });
                        shapes.Add(Circle(new Color(1f, 1f, 1f, .6f), x - 3f, 29f, 2.6f));
                    }
                    shapes.Add(Ring(style.PaperLine, x, 32, 12.5f, 1.6f));
                }
                // The rule is a circuit trace with contact pads.
                shapes.Add(Poly(style.Accent, (left, ruleY - 3), (left + 6, ruleY - 3), (left + 6, ruleY + 3), (left, ruleY + 3)));
                shapes.Add(Poly(style.Accent, (right - 6, ruleY - 3), (right, ruleY - 3), (right, ruleY + 3), (right - 6, ruleY + 3)));
                shapes.Add(Poly(style.Accent, (middle - 3, ruleY - 3), (middle + 3, ruleY - 3), (middle + 3, ruleY + 3), (middle - 3, ruleY + 3)));
                break;
            }
            case "regent":
            {
                // A crown with jewels and a small constellation trailing from it.
                shapes.Add(Poly(style.Accent, (502, 38), (498, 20), (514, 30), (528, 14), (542, 30), (558, 20), (554, 38)));
                shapes.Add(Poly(style.Gold, (502, 39.5f), (554, 39.5f), (554, 45), (502, 45)));
                shapes.Add(Circle(style.Gold, 498, 19, 2.4f));
                shapes.Add(Circle(style.Gold, 528, 13, 2.6f));
                shapes.Add(Circle(style.Gold, 558, 19, 2.4f));
                shapes.Add(Star(new Color("e85c7a"), 528, 42.2f, 2.6f));
                var star = new Color("ffe9a8");
                shapes.Add(Star(star, 470, 23, 7.5f) with { Anim = "pulse" });
                shapes.Add(Star(star, 448, 39, 4.8f) with { Anim = "pulse", Delay = .5f });
                shapes.Add(Star(star, 428, 20, 3.4f) with { Anim = "pulse", Delay = 1f });
                shapes.Add(Star(star, 410, 35, 2.5f) with { Anim = "pulse", Delay = 1.5f });
                shapes.Add(Star(star, 392, 24, 1.8f) with { Anim = "pulse", Delay = 2f });
                // A star in the middle of the rule.
                shapes.Add(Star(style.Accent, middle, ruleY, 7f));
                break;
            }
            case "necrobinder":
            {
                var bone = new Color("3b2f4a");
                // A skull whose eyes glow, and souls rising beside it.
                shapes.Add(Ellipse(bone, 538, 28, 13.5f, 12.5f));
                shapes.Add(Poly(bone, (529, 36), (547, 36), (546, 46), (530, 46)));
                shapes.Add(Ellipse(style.Gold, 532.5f, 28.5f, 3.6f, 4.2f) with { Anim = "pulse" });
                shapes.Add(Ellipse(style.Gold, 543.5f, 28.5f, 3.6f, 4.2f) with { Anim = "pulse" });
                shapes.Add(Poly(style.Paper, (538, 32.5f), (540.2f, 37), (535.8f, 37)));
                foreach (var x in new[] { 534f, 538f, 542f })
                    shapes.Add(new("line", [new(x, 40.5f), new(x, 46)], style.Paper, 1.2f));
                shapes.Add(Flame(style.Gold, 506, 38, 6f) with { Anim = "rise" });
                shapes.Add(Flame(style.Gold, 489, 42, 4.4f) with { Anim = "rise", Delay = .9f });
                shapes.Add(Flame(style.Gold, 474, 36, 3.2f) with { Anim = "rise", Delay = 1.8f });
                // Vertebrae in the middle of the rule.
                for (var i = -2; i <= 2; i++) shapes.Add(Ellipse(style.InkSoft, middle + i * 12f, ruleY, 4f, 3f));
                break;
            }
        }
        return shapes;
    }

    private static OrnamentShape Poly(Color color, params (float X, float Y)[] points) =>
        new("poly", points.Select(p => new Vector2(p.X, p.Y)).ToArray(), color);

    private static OrnamentShape Ellipse(Color color, float x, float y, float rx, float ry, int segments = 24) =>
        new("poly", Enumerable.Range(0, segments).Select(i => new Vector2(
            x + rx * MathF.Cos(i * MathF.Tau / segments), y + ry * MathF.Sin(i * MathF.Tau / segments))).ToArray(), color);

    private static OrnamentShape Circle(Color color, float x, float y, float radius) => Ellipse(color, x, y, radius, radius);

    private static OrnamentShape Ring(Color color, float x, float y, float radius, float width)
    {
        var points = Enumerable.Range(0, 33).Select(i => new Vector2(x + radius * MathF.Cos(i * MathF.Tau / 32), y + radius * MathF.Sin(i * MathF.Tau / 32))).ToArray();
        return new("line", points, color, width);
    }

    // Four-point star (a spark, a blade tip, a jewel).
    private static OrnamentShape Star(Color color, float x, float y, float radius)
    {
        var inner = radius * .34f;
        return new("poly", Enumerable.Range(0, 8).Select(i =>
        {
            var r = i % 2 == 0 ? radius : inner;
            return new Vector2(x + r * MathF.Cos(i * MathF.Tau / 8 - MathF.PI / 2), y + r * MathF.Sin(i * MathF.Tau / 8 - MathF.PI / 2));
        }).ToArray(), color);
    }

    // A drop: pointed at the top, round at the bottom.
    private static OrnamentShape Drop(Color color, float x, float y, float radius)
    {
        var points = new List<Vector2> { new(x, y - radius * 2.2f) };
        for (var i = 0; i <= 12; i++)
        {
            var angle = -MathF.PI / 6 + i * (MathF.PI + MathF.PI / 3) / 12;
            points.Add(new Vector2(x + radius * MathF.Cos(angle), y + radius * MathF.Sin(angle)));
        }
        return new("poly", points.ToArray(), color);
    }

    // A soul wisp: a drop with a tip that leans to the side.
    private static OrnamentShape Flame(Color color, float x, float y, float radius)
    {
        var drop = Drop(color, x, y, radius);
        drop.Points[0] = new Vector2(x + radius * .7f, y - radius * 2.6f);
        return drop;
    }
}
