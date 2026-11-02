using Godot;

namespace BuildAdvisor.UI;

// 6.24 "Parchment" look (variant B, chosen by the owner): parchment panel on the left, a red wax
// seal "BD" at the left edge instead of the emblem in the game's top bar, paper ribbons as badges.
// Fonts ship with the mod (SIL OFL): PT Serif (body, Cyrillic), Forum (headings, Cyrillic),
// Cinzel (the BD monogram). A missing font file falls back to the game's default font.
internal static class AdvisorTheme
{
    internal static readonly Color Paper = new("e9dcc0");
    internal static readonly Color PaperDark = new("ddcba5");
    internal static readonly Color PaperLine = new("b9a27a");
    internal static readonly Color Ink = new("3a2a1a");
    internal static readonly Color InkSoft = new("6b5639");
    internal static readonly Color Wax = new("8e2b22");
    internal static readonly Color Gold = new("c9a24a");
    internal static readonly Color Cream = new("f6e7c9");
    internal static readonly Color Good = new("2f6b3a");

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
        var box = Box(Paper, PaperLine, 2, 6);
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

    // Card, shop and selection badges: a paper ribbon; the best option is a red ribbon in gold.
    internal static void Ribbon(Label label, bool best, int fontSize = 15)
    {
        if (Bold is { } font) label.AddThemeFontOverride("font", font);
        label.AddThemeFontSizeOverride("font_size", fontSize);
        label.AddThemeColorOverride("font_color", best ? Cream : Ink);
        label.AddThemeStyleboxOverride("normal", best ? Box(Wax, Gold, 2, 4) : Box(Paper, PaperLine, 1, 4));
    }

    // Hover cards: parchment with dark ink; the accent colours the frame.
    internal static void Popup(Label label, Color? accent = null)
    {
        if (Body is { } font) label.AddThemeFontOverride("font", font);
        label.AddThemeFontSizeOverride("font_size", 15);
        label.AddThemeColorOverride("font_color", Ink);
        var box = Box(Paper, accent ?? PaperLine, 2, 6);
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
            theme.SetStylebox("normal", type, Box(PaperDark, PaperLine));
            theme.SetStylebox("hover", type, Box(new Color("d4bf94"), Wax));
            theme.SetStylebox("pressed", type, Box(new Color("c9b083"), Wax));
            theme.SetStylebox("focus", type, Box(new Color(0, 0, 0, 0), PaperLine));
        }
        theme.SetStylebox("normal", "LineEdit", Box(new Color("f3ead6"), PaperLine));
        theme.SetStylebox("focus", "LineEdit", Box(new Color("f3ead6"), Wax));
        theme.SetStylebox("panel", "PopupMenu", Box(Paper, PaperLine, 2, 4));
        theme.SetStylebox("hover", "PopupMenu", Box(PaperDark, PaperDark, 0, 2));
        theme.SetStylebox("panel", "PanelContainer", Box(new Color("e2d2b1"), new Color(0, 0, 0, 0), 0, 4));
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
            var box = Box(fill, Gold, 3, 37);
            box.ContentMarginLeft = 18; box.ContentMarginRight = 4;
            box.ShadowColor = new Color(0, 0, 0, .45f); box.ShadowSize = 8; box.ShadowOffset = new Vector2(0, 4);
            return box;
        }
        seal.AddThemeStyleboxOverride("normal", Round(Wax));
        seal.AddThemeStyleboxOverride("hover", Round(new Color("a3352a")));
        seal.AddThemeStyleboxOverride("pressed", Round(new Color("6f2019")));
        seal.AddThemeStyleboxOverride("focus", Round(Wax));
        return seal;
    }
}
