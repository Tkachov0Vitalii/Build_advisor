using Godot;

namespace BuildAdvisor.UI;

// 6.24 "Parchment" look (variant B, chosen by the owner): parchment panel on the left, a red wax
// seal "BD" at the left edge instead of the emblem in the game's top bar, paper ribbons as badges.
// Fonts ship with the mod (SIL OFL): PT Serif (body, Cyrillic), Forum (headings, Cyrillic),
// Cinzel (the BD monogram). A missing font file falls back to the game's default font.
internal static class AdvisorTheme
{
    // 7.3: the colours come from the selected style (UiStyles); "Wax" is the style's accent.
    internal static UiStyle Style => UiStyles.Current;
    internal static Color Paper => Style.Paper;
    internal static Color PaperDark => Style.PaperDark;
    internal static Color PaperLine => Style.PaperLine;
    internal static Color Ink => Style.Ink;
    internal static Color InkSoft => Style.InkSoft;
    internal static Color Wax => Style.Accent;
    internal static Color Gold => Style.Gold;
    internal static Color Cream => Style.SealInk;
    internal static Color Rule => Style.Rule;

    private static readonly Lazy<Font?> BodyFont = new(() => Load("PTSerif-Regular.ttf"));
    private static readonly Lazy<Font?> BoldFont = new(() => Load("PTSerif-Bold.ttf"));
    private static readonly Lazy<Font?> HeadingFont = new(() => Load("Forum-Regular.ttf"));
    private static readonly Lazy<Font?> MonogramFont = new(() =>
    {
        var cinzel = Load("Cinzel-Variable.ttf");
        if (cinzel is null) return null;
        // Variable font: the bold instance for the seal.
        var bold = new FontVariation { BaseFont = cinzel };
        bold.VariationOpentype = new Godot.Collections.Dictionary { { TextServerManager.GetPrimaryInterface().NameToTag("wght"), 800 } };
        return bold;
    });

    internal static Font? Body => BodyFont.Value;
    internal static Font? Bold => BoldFont.Value ?? BodyFont.Value;
    internal static Font? Heading => HeadingFont.Value ?? BodyFont.Value;
    internal static Font? Monogram => MonogramFont.Value ?? BoldFont.Value;

    private static Font? Load(string name)
    {
        try
        {
            var folder = Path.GetDirectoryName(typeof(AdvisorTheme).Assembly.Location);
            if (string.IsNullOrWhiteSpace(folder)) return null;
            var path = Path.Combine(folder, "fonts", name);
            if (!File.Exists(path)) return null;
            var font = new FontFile();
            return font.LoadDynamicFont(path) == Error.Ok ? font : null;
        }
        catch { return null; }
    }

    internal static StyleBoxFlat Box(Color fill, Color border, int borderWidth = 1, int radius = 4)
    {
        var box = new StyleBoxFlat
        {
            BgColor = fill, BorderColor = border,
            BorderWidthLeft = borderWidth, BorderWidthTop = borderWidth, BorderWidthRight = borderWidth, BorderWidthBottom = borderWidth,
            CornerRadiusTopLeft = radius, CornerRadiusTopRight = radius, CornerRadiusBottomLeft = radius, CornerRadiusBottomRight = radius,
            ContentMarginLeft = 10, ContentMarginRight = 10, ContentMarginTop = 4, ContentMarginBottom = 4
        };
        return box;
    }

    internal static StyleBoxFlat PanelBox()
    {
        var box = Box(Paper, PaperLine, Style.PanelBorder, Style.PanelRadius);
        box.ShadowColor = new Color(0, 0, 0, .55f);
        box.ShadowSize = 18;
        box.ShadowOffset = new Vector2(0, 8);
        return box;
    }

    // Underline-only box for the active tab.
    internal static StyleBoxFlat TabBox(bool active)
    {
        var box = new StyleBoxFlat
        {
            BgColor = new Color(0, 0, 0, 0), BorderColor = Wax, BorderWidthBottom = active ? 2 : 0,
            ContentMarginLeft = 2, ContentMarginRight = 2, ContentMarginTop = 2, ContentMarginBottom = 4
        };
        return box;
    }

    // 7.0: every tier has its own colour (owner: "the letter gets lost on the colour"): a light
    // tinted ribbon, dark ink of the same hue (contrast ≥ 7:1, regression-checked) and a strong
    // frame. S purple, A red, B amber, C blue, D grey; the best option adds a gold frame and ★.
    internal static (Color Fill, Color Ink, Color Frame) TierColors(string tier) => tier switch
    {
        "S" => (new Color("e6d5f2"), new Color("3f1260"), new Color("7d3fb0")),
        "A" => (new Color("f4d6cf"), new Color("6b1710"), new Color("b0302a")),
        "B" => (new Color("f5e2c0"), new Color("5a3203"), new Color("c27a14")),
        "C" => (new Color("d7e4f2"), new Color("17375f"), new Color("3a72b5")),
        _ => (new Color("e3ded6"), new Color("35302a"), new Color("8a8178"))
    };

    // WCAG contrast ratio of two colours (1..21).
    internal static double Contrast(Color a, Color b)
    {
        static double Channel(float c) => c <= .03928 ? c / 12.92 : Math.Pow((c + .055) / 1.055, 2.4);
        static double Luminance(Color c) => .2126 * Channel(c.R) + .7152 * Channel(c.G) + .0722 * Channel(c.B);
        var (light, dark) = (Math.Max(Luminance(a), Luminance(b)), Math.Min(Luminance(a), Luminance(b)));
        return (light + .05) / (dark + .05);
    }

    // Card, shop and selection badges: a ribbon in the tier's colours; the best option is framed in gold.
    internal static void Ribbon(Label label, string tier, bool best, int fontSize = 15)
    {
        var (fill, ink, frame) = TierColors(tier);
        if (Bold is { } font) label.AddThemeFontOverride("font", font);
        label.AddThemeFontSizeOverride("font_size", fontSize);
        label.AddThemeColorOverride("font_color", ink);
        var box = Box(fill, best ? Gold : frame, best ? 3 : 2, 4);
        if (best) { box.ShadowColor = new Color(Gold, .45f); box.ShadowSize = 6; }
        label.AddThemeStyleboxOverride("normal", box);
    }

    internal static Color TierFrame(string tier) => TierColors(tier).Frame;

    // Hover cards: parchment with dark ink; the accent colours the frame.
    internal static void Popup(Label label, Color? accent = null)
    {
        if (Body is { } font) label.AddThemeFontOverride("font", font);
        label.AddThemeFontSizeOverride("font_size", 15);
        label.AddThemeColorOverride("font_color", Ink);
        var box = Box(Paper, accent ?? PaperLine, 2, Math.Max(2, Style.PanelRadius));
        box.ShadowColor = new Color(0, 0, 0, .5f);
        box.ShadowSize = 12;
        label.AddThemeStyleboxOverride("normal", box);
    }

    // The theme of the panel: every control inside inherits it.
    internal static Theme PanelTheme()
    {
        var theme = new Theme();
        if (Body is { } body) theme.DefaultFont = body;
        theme.DefaultFontSize = 17;
        foreach (var type in new[] { "Label", "Button", "OptionButton", "CheckButton", "LineEdit", "PopupMenu" })
            theme.SetColor("font_color", type, Ink);
        theme.SetColor("font_hover_color", "Button", Wax);
        theme.SetColor("font_pressed_color", "Button", Wax);
        theme.SetColor("font_focus_color", "Button", Ink);
        theme.SetColor("font_hover_color", "OptionButton", Wax);
        theme.SetColor("font_hover_color", "CheckButton", Wax);
        theme.SetColor("font_pressed_color", "CheckButton", Ink);
        theme.SetColor("font_placeholder_color", "LineEdit", InkSoft);
        theme.SetColor("caret_color", "LineEdit", Ink);
        theme.SetColor("font_hover_color", "PopupMenu", Wax);
        foreach (var type in new[] { "Button", "OptionButton" })
        {
            theme.SetStylebox("normal", type, Box(PaperDark, PaperLine, 1, Style.ControlRadius));
            theme.SetStylebox("hover", type, Box(Style.Hover, Wax, 1, Style.ControlRadius));
            theme.SetStylebox("pressed", type, Box(Style.Pressed, Wax, 1, Style.ControlRadius));
            theme.SetStylebox("focus", type, Box(new Color(0, 0, 0, 0), PaperLine, 1, Style.ControlRadius));
        }
        theme.SetStylebox("normal", "LineEdit", Box(Style.Field, PaperLine, 1, Style.ControlRadius));
        theme.SetStylebox("focus", "LineEdit", Box(Style.Field, Wax, 1, Style.ControlRadius));
        theme.SetStylebox("panel", "PopupMenu", Box(Paper, PaperLine, 2, Style.ControlRadius));
        theme.SetStylebox("hover", "PopupMenu", Box(PaperDark, PaperDark, 0, 2));
        theme.SetColor("font_hover_color", "PopupMenu", Wax);
        theme.SetStylebox("panel", "PanelContainer", Box(Style.Section, new Color(0, 0, 0, 0), 0, Style.ControlRadius));
        theme.SetStylebox("scroll", "VScrollBar", Box(PaperDark, PaperDark, 0, 3));
        theme.SetStylebox("grabber", "VScrollBar", Box(PaperLine, PaperLine, 0, 3));
        theme.SetStylebox("grabber_highlight", "VScrollBar", Box(InkSoft, InkSoft, 0, 3));
        theme.SetStylebox("grabber_pressed", "VScrollBar", Box(Wax, Wax, 0, 3));
        return theme;
    }

    // The red wax seal with the gold rim; half of it is tucked behind the left edge of the screen.
    internal static Button Seal(string name)
    {
        var seal = new Button
        {
            Name = name, Text = "BD", Position = new Vector2(-14, 500), Size = new Vector2(74, 74),
            CustomMinimumSize = new Vector2(74, 74), ZIndex = 500, FocusMode = Control.FocusModeEnum.None,
            Alignment = HorizontalAlignment.Center
        };
        if (Monogram is { } font) seal.AddThemeFontOverride("font", font);
        seal.AddThemeFontSizeOverride("font_size", 24);
        foreach (var state in new[] { "font_color", "font_hover_color", "font_pressed_color", "font_focus_color" })
            seal.AddThemeColorOverride(state, Cream);
        StyleBoxFlat Round(Color fill)
        {
            var box = Box(fill, Style.SealRim, 3, 37);
            // 7.3: the seal's silhouette belongs to the style (round wax, forged square, blade, bone).
            (box.CornerRadiusTopLeft, box.CornerRadiusTopRight, box.CornerRadiusBottomRight, box.CornerRadiusBottomLeft) = Style.SealRadii;
            box.ContentMarginLeft = 18; box.ContentMarginRight = 4;
            box.ShadowColor = new Color(0, 0, 0, .45f); box.ShadowSize = 8; box.ShadowOffset = new Vector2(0, 4);
            return box;
        }
        seal.AddThemeStyleboxOverride("normal", Round(Style.SealFill));
        seal.AddThemeStyleboxOverride("hover", Round(Style.SealHover));
        seal.AddThemeStyleboxOverride("pressed", Round(Style.SealPressed));
        seal.AddThemeStyleboxOverride("focus", Round(Style.SealFill));
        return seal;
    }

    internal const string OrnamentName = "BuildAdvisorOrnament";

    // 7.3: the style's small details (UiOrnaments) as Polygon2D / Line2D children of the panel.
    // They draw above the panel and take no mouse input. A failure here must never cost the panel.
    internal static void Decorate(Control page, float ruleY)
    {
        try
        {
            var shapes = UiOrnaments.For(Style, page.Size.X, page.Size.Y, ruleY);
            if (shapes.Count == 0) return;
            var layer = new Node2D { Name = OrnamentName };
            foreach (var shape in shapes)
            {
                Node2D node = shape.Kind == "line"
                    ? new Line2D { Points = shape.Points, DefaultColor = shape.Color, Width = shape.Width, Antialiased = true }
                    : new Polygon2D { Polygon = shape.Points, Color = shape.Color, Antialiased = true };
                if (shape.Anim.Length > 0) { node.SetMeta("ba_anim", shape.Anim); node.SetMeta("ba_delay", shape.Delay); }
                layer.AddChild(node);
            }
            page.AddChild(layer);
        }
        catch (Exception error) { GD.Print("Build Advisor ornament: " + error.Message); }
    }

    // Tweens need the nodes inside the scene tree, so this runs after the panel is attached.
    // They are bound to their node and end with it.
    internal static void Animate(Control page)
    {
        try
        {
            if (page.GetNodeOrNull<Node2D>(OrnamentName) is not { } layer || !layer.IsInsideTree()) return;
            foreach (var node in layer.GetChildren().OfType<Node2D>())
            {
                if (!node.HasMeta("ba_anim")) continue;
                var delay = (double)node.GetMeta("ba_delay").AsSingle();
                var tween = node.CreateTween().SetLoops();
                switch (node.GetMeta("ba_anim").AsString())
                {
                    case "pulse":
                        tween.TweenInterval(delay + .01);
                        tween.TweenProperty(node, "modulate:a", .35f, 1.1).SetTrans(Tween.TransitionType.Sine);
                        tween.TweenProperty(node, "modulate:a", 1f, 1.1).SetTrans(Tween.TransitionType.Sine);
                        break;
                    case "rise":
                        tween.TweenInterval(delay + .01);
                        tween.TweenProperty(node, "position:y", -9f, 2.2).From(0f);
                        tween.Parallel().TweenProperty(node, "modulate:a", 0f, 2.2).From(.95f);
                        break;
                    case "fall":
                        tween.TweenInterval(delay + .6);
                        tween.TweenProperty(node, "position:y", 7f, 1.2).From(0f).SetEase(Tween.EaseType.In).SetTrans(Tween.TransitionType.Quad);
                        tween.Parallel().TweenProperty(node, "modulate:a", 0f, 1.2).From(1f);
                        break;
                }
            }
        }
        catch (Exception error) { GD.Print("Build Advisor ornament animation: " + error.Message); }
    }
}
