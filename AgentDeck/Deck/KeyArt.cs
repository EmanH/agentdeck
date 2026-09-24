using AgentDeck.Core;
using AgentDeck.Dictation;
using OpenMacroBoard.SDK;
using SkiaSharp;
using Svg.Skia;

namespace AgentDeck.Deck;

/// <summary>Draws 72x72 Stream Deck key images.</summary>
static class KeyArt
{
    public const int Size = 72;

    static readonly SKTypeface Bold = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold);
    static readonly SKTypeface Semibold = SKTypeface.FromFamilyName("Segoe UI Semibold", SKFontStyle.Normal);
    static readonly SKTypeface Mono = SKTypeface.FromFamilyName("Cascadia Mono", SKFontStyle.Bold);
    static readonly Dictionary<AgentKind, SKPicture?> Logos = new();

    static readonly SKColor Green = new(0, 190, 90), Amber = new(255, 176, 0), Red = new(220, 50, 50);

    public static KeyBitmap Render(Action<SKCanvas> draw)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(Size, Size, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Black);
            draw(canvas);
        }
        return KeyBitmap.Create.FromBgra32Array(Size, Size, bitmap.GetPixelSpan());
    }

    /// <summary>Dev aid (AgentDeck.exe --preview-keys dir): render sample keys to a PNG contact sheet.</summary>
    public static void WritePreview(string path)
    {
        var project = new Project { Name = "Stream Deck", Color = "#3b82f6", Icon = "joystick" };
        var claude = new Session { Id = 1, ProjectId = "", Agent = AgentKind.Claude, DefaultName = "Claude", Title = "Fix Login Bug" };
        var codex = new Session { Id = 2, ProjectId = "", Agent = AgentKind.Codex, DefaultName = "Codex", Summary = "Deploy Script" };
        var blue = ParseColor(project.Color);
        Action<SKCanvas>[] keys =
        [
            c => Project(c, project, true, 2),
            c => Project(c, new Project { Name = "Website", Color = "#22c55e" }, false, 1, hasDone: true),
            c => Session(c, codex, blue, false, -1, done: true),
            c => MoreProjects(c, ["#f59e0b", "#ef4444", "#a855f7"]),
            Back,
            NextPage,
            c => Session(c, claude, blue, true, 0.8f),
            c => { Session(c, codex, blue, false, -1); HoldRing(c, 0.45f); },
            c => { Session(c, codex, blue, false, -1); HoldRing(c, 1f); },
            c => Launcher(c, AgentKind.Grok),
            c => Repaste(c, 0.65f),
            WorkflowsButton,
            c => Workflow(c, new Workflow { Name = "Review Open PRs", Agent = AgentKind.Claude, Color = "#a855f7", Icon = "magnifying-glass-tilted-left" }),
            c => Workflow(c, new Workflow { Name = "Update Deps", Agent = AgentKind.Codex, Color = "#14b8a6", Icon = "package" }),
            c => Workflow(c, new Workflow { Name = "Ship It", Agent = AgentKind.Grok, Color = "#f97316", Icon = "rocket" }),
            c => Project(c, new Project { Name = "Website", Color = "#22c55e", Icon = "globe-showing-americas" }, false, 2),
            NewWorkflow,
            c => Mic(c, DictationState.Idle, 0, 0),
        ];
        using var sheet = new SKBitmap(keys.Length * 80, 80);
        using (var canvas = new SKCanvas(sheet))
        {
            canvas.Clear(new SKColor(40, 40, 40));
            for (int i = 0; i < keys.Length; i++)
            {
                using var key = new SKBitmap(Size, Size);
                using (var kc = new SKCanvas(key)) { kc.Clear(SKColors.Black); keys[i](kc); }
                using var image = SKImage.FromBitmap(key);
                canvas.DrawImage(image, i * 80 + 4, 4);
            }
        }
        using var file = File.Create(path);
        sheet.Encode(file, SKEncodedImageFormat.Png, 100);
    }

    public static SKColor ParseColor(string hex) => SKColor.TryParse(hex, out var c) ? c : new SKColor(59, 130, 246);

    static SKPaint Fill(SKColor color) => new() { Color = color, IsAntialias = true, Style = SKPaintStyle.Fill };
    static SKPaint Stroke(SKColor color, float width) =>
        new() { Color = color, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = width, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };

    // --- logos ------------------------------------------------------------------

    static SKPicture? Logo(AgentKind agent)
    {
        lock (Logos)
        {
            if (Logos.TryGetValue(agent, out var cached)) return cached;
            SKPicture? picture = null;
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "icons", $"{agent.ToString().ToLowerInvariant()}.svg");
            if (File.Exists(path))
            {
                try
                {
                    var svg = new SKSvg();
                    using var stream = File.OpenRead(path);
                    picture = svg.Load(stream);
                }
                catch (Exception ex) { Log.Error($"load logo {agent}", ex); }
            }
            return Logos[agent] = picture;
        }
    }

    static void DrawLogo(SKCanvas canvas, AgentKind agent, SKRect rect, byte alpha = 255)
    {
        var picture = agent == AgentKind.Shell ? null : Logo(agent);
        if (picture == null)
        {
            using var font = new SKFont(Mono, rect.Height * 0.62f);
            using var paint = Fill(new SKColor(210, 210, 210, alpha));
            canvas.DrawText(">_", rect.MidX, rect.MidY + font.Size * 0.36f, SKTextAlign.Center, font, paint);
            return;
        }
        var bounds = picture.CullRect;
        float scale = Math.Min(rect.Width / bounds.Width, rect.Height / bounds.Height);
        canvas.Save();
        if (alpha < 255) canvas.SaveLayer(new SKPaint { Color = SKColors.White.WithAlpha(alpha) });
        canvas.Translate(rect.MidX - bounds.Width * scale / 2 - bounds.Left * scale, rect.MidY - bounds.Height * scale / 2 - bounds.Top * scale);
        canvas.Scale(scale);
        canvas.DrawPicture(picture);
        if (alpha < 255) canvas.Restore();
        canvas.Restore();
    }

    // --- text -------------------------------------------------------------------

    /// <summary>Draw text as large as possible within rect, wrapping onto at most maxLines lines.</summary>
    static void DrawFittedText(SKCanvas canvas, string text, SKRect rect, float maxSize, float minSize, int maxLines,
                               SKColor color, SKTypeface? typeface = null)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return;
        typeface ??= Bold;
        List<string>? lines = null;
        float size = maxSize;
        for (; size >= minSize; size -= 0.5f)
        {
            using var probe = new SKFont(typeface, size);
            lines = Wrap(words, probe, rect.Width, maxLines);
            if (lines != null) break;
        }
        size = Math.Max(size, minSize);
        using var font = new SKFont(typeface, size) { Edging = SKFontEdging.Antialias };
        lines ??= Wrap(words, font, rect.Width, maxLines, force: true)!;

        float lineHeight = size * 1.08f;
        float top = rect.MidY - lineHeight * lines.Count / 2;
        using var paint = Fill(color);
        for (int i = 0; i < lines.Count; i++)
            canvas.DrawText(lines[i], rect.MidX, top + lineHeight * i + size * 0.8f, SKTextAlign.Center, font, paint);
    }

    static List<string>? Wrap(string[] words, SKFont font, float width, int maxLines, bool force = false)
    {
        var lines = new List<string>();
        var current = "";
        foreach (var word in words)
        {
            var candidate = current.Length == 0 ? word : current + " " + word;
            if (font.MeasureText(candidate) <= width) { current = candidate; continue; }
            if (current.Length > 0) lines.Add(current);
            current = word;
            if (!force && font.MeasureText(word) > width) return null;
        }
        if (current.Length > 0) lines.Add(current);
        if (lines.Count <= maxLines) return lines;
        if (!force) return null;
        lines = lines.Take(maxLines).ToList();
        var last = lines[^1];
        while (last.Length > 1 && font.MeasureText(last + "…") > width) last = last[..^1];
        lines[^1] = last + "…";
        return lines;
    }

    // --- keys -------------------------------------------------------------------

    public static void Project(SKCanvas canvas, Project project, bool selected, int sessionCount, bool hasDone = false)
    {
        DrawProject(canvas, project, selected, sessionCount);
        if (hasDone) Star(canvas, 61, 17, 15);
    }

    /// <summary>Draw a Fluent Emoji icon by name, fitted into rect. Returns false if there's no such icon.</summary>
    static bool DrawIcon(SKCanvas canvas, string? name, SKRect rect)
    {
        var picture = IconLibrary.Picture(name);
        if (picture == null) return false;
        var bounds = picture.CullRect;
        float scale = Math.Min(rect.Width / bounds.Width, rect.Height / bounds.Height);
        canvas.Save();
        canvas.Translate(rect.MidX - bounds.Width * scale / 2 - bounds.Left * scale, rect.MidY - bounds.Height * scale / 2 - bounds.Top * scale);
        canvas.Scale(scale);
        canvas.DrawPicture(picture);
        canvas.Restore();
        return true;
    }

    static void DrawProject(SKCanvas canvas, Project project, bool selected, int sessionCount)
    {
        if (IconLibrary.Exists(project.Icon))
        {
            DrawProjectWithIcon(canvas, project, selected, sessionCount);
            return;
        }
        var color = ParseColor(project.Color);
        var rect = new SKRect(3, 3, 69, 69);
        if (selected)
        {
            using var fill = Fill(color);
            canvas.DrawRoundRect(rect, 11, 11, fill);
        }
        else
        {
            using var fill = Fill(new SKColor((byte)(color.Red * 0.22), (byte)(color.Green * 0.22), (byte)(color.Blue * 0.22)));
            canvas.DrawRoundRect(rect, 11, 11, fill);
            using var border = Stroke(color.WithAlpha(200), 1.5f);
            canvas.DrawRoundRect(new SKRect(3.75f, 3.75f, 68.25f, 68.25f), 10.5f, 10.5f, border);
        }
        var textColor = selected ? SKColors.White : new SKColor(235, 235, 235);
        DrawFittedText(canvas, project.Name, new SKRect(7, 8, 65, sessionCount > 0 ? 56 : 64), 19, 10, 3, textColor);

        // One dot per open terminal.
        int dots = Math.Min(sessionCount, 6);
        using var dotPaint = Fill(SKColors.White.WithAlpha(selected ? (byte)230 : (byte)170));
        float x0 = 36 - (dots - 1) * 3.5f;
        for (int i = 0; i < dots; i++) canvas.DrawCircle(x0 + i * 7, 62, 2.1f, dotPaint);
    }

    /// <summary>Project key with its icon on top and the name beneath.</summary>
    static void DrawProjectWithIcon(SKCanvas canvas, Project project, bool selected, int sessionCount)
    {
        var color = ParseColor(project.Color);
        var rect = new SKRect(3, 3, 69, 69);
        if (selected)
        {
            using var fill = Fill(color);
            canvas.DrawRoundRect(rect, 11, 11, fill);
        }
        else
        {
            using var fill = Fill(new SKColor((byte)(color.Red * 0.22), (byte)(color.Green * 0.22), (byte)(color.Blue * 0.22)));
            canvas.DrawRoundRect(rect, 11, 11, fill);
            using var border = Stroke(color.WithAlpha(200), 1.5f);
            canvas.DrawRoundRect(new SKRect(3.75f, 3.75f, 68.25f, 68.25f), 10.5f, 10.5f, border);
        }
        DrawIcon(canvas, project.Icon, new SKRect(23, 7, 49, 33));
        var textColor = selected ? SKColors.White : new SKColor(235, 235, 235);
        DrawFittedText(canvas, project.Name, new SKRect(6, 35, 66, sessionCount > 0 ? 59 : 65), 14, 9, 2, textColor);
        int dots = Math.Min(sessionCount, 6);
        using var dotPaint = Fill(SKColors.White.WithAlpha(selected ? (byte)230 : (byte)170));
        float x0 = 36 - (dots - 1) * 3.5f;
        for (int i = 0; i < dots; i++) canvas.DrawCircle(x0 + i * 7, 63, 2, dotPaint);
    }

    public static void AddProject(SKCanvas canvas)
    {
        using var border = Stroke(new SKColor(90, 90, 90), 1.5f);
        border.PathEffect = SKPathEffect.CreateDash([4, 4], 0);
        canvas.DrawRoundRect(new SKRect(4, 4, 68, 68), 10, 10, border);
        using var plus = Stroke(new SKColor(200, 200, 200), 2.5f);
        canvas.DrawLine(36, 18, 36, 38, plus);
        canvas.DrawLine(26, 28, 46, 28, plus);
        DrawFittedText(canvas, "Add project", new SKRect(6, 44, 66, 64), 11, 9, 1, new SKColor(170, 170, 170), Semibold);
    }

    static readonly SKTypeface Symbols = SKTypeface.FromFamilyName("Segoe UI Symbol");
    static readonly SKColor StarColor = new(255, 201, 64);

    /// <summary>The "finished, go look" sparkle.</summary>
    public static void Star(SKCanvas canvas, float x, float y, float size = 17)
    {
        using var font = new SKFont(Symbols, size);
        using var glow = Fill(StarColor.WithAlpha(70));
        glow.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 3);
        canvas.DrawText("✦", x, y, SKTextAlign.Center, font, glow);
        using var paint = Fill(StarColor);
        canvas.DrawText("✦", x, y, SKTextAlign.Center, font, paint);
    }

    public static void Session(SKCanvas canvas, Session session, SKColor projectColor, bool active, float busyPulse, bool done = false)
    {
        if (done) Star(canvas, 12, 19);
        if (active)
        {
            using var border = Stroke(projectColor, 2.2f);
            canvas.DrawRoundRect(new SKRect(2.5f, 2.5f, 69.5f, 69.5f), 11, 11, border);
        }
        DrawLogo(canvas, session.Agent, new SKRect(25, 7, 47, 29));
        DrawFittedText(canvas, session.Label, new SKRect(5, 32, 67, 68), 16, 9, 2, SKColors.White);
        if (busyPulse >= 0)
        {
            using var dot = Fill(projectColor.WithAlpha((byte)(90 + 165 * busyPulse)));
            canvas.DrawCircle(62, 10, 3.4f, dot);
        }
    }

    /// <summary>Red ring that fills clockwise while a key is held to close its terminal.</summary>
    public static void HoldRing(SKCanvas canvas, float progress)
    {
        if (progress <= 0) return;
        using var dim = Fill(new SKColor(0, 0, 0, (byte)(110 * progress)));
        canvas.DrawRoundRect(new SKRect(0, 0, 72, 72), 12, 12, dim);
        using var track = Stroke(new SKColor(90, 30, 30), 3);
        canvas.DrawCircle(36, 36, 30, track);
        using var ring = Stroke(Red, 3.5f);
        canvas.DrawArc(new SKRect(6, 6, 66, 66), -90, 360 * progress, false, ring);
        if (progress >= 0.999f)
        {
            using var x = Stroke(SKColors.White, 3);
            canvas.DrawLine(28, 28, 44, 44, x);
            canvas.DrawLine(44, 28, 28, 44, x);
        }
    }

    /// <summary>"More projects": a 2x2 grid of rounded tiles in the hidden projects' colors.</summary>
    public static void MoreProjects(SKCanvas canvas, string[] colors)
    {
        for (int i = 0; i < 4; i++)
        {
            float x = 20 + i % 2 * 17, y = 20 + i / 2 * 17;
            var rect = new SKRect(x, y, x + 14, y + 14);
            if (i < colors.Length)
            {
                using var fill = Fill(ParseColor(colors[i]));
                canvas.DrawRoundRect(rect, 4, 4, fill);
            }
            else
            {
                using var outline = Stroke(new SKColor(90, 90, 90), 1.4f);
                canvas.DrawRoundRect(rect, 4, 4, outline);
            }
        }
    }

    static void Chevron(SKCanvas canvas, bool left)
    {
        using var paint = Stroke(new SKColor(225, 225, 225), 3.2f);
        float dir = left ? -1 : 1;
        canvas.DrawLine(36 - 6 * dir, 23, 36 + 7 * dir, 36, paint);
        canvas.DrawLine(36 + 7 * dir, 36, 36 - 6 * dir, 49, paint);
    }

    public static void Back(SKCanvas canvas)
    {
        using var disc = Fill(new SKColor(38, 38, 38));
        canvas.DrawCircle(36, 36, 25, disc);
        Chevron(canvas, left: true);
    }

    public static void NextPage(SKCanvas canvas)
    {
        using var disc = Fill(new SKColor(38, 38, 38));
        canvas.DrawCircle(36, 36, 25, disc);
        Chevron(canvas, left: false);
    }

    /// <summary>"Paste again": clipboard glyph with a ring that runs down as the key's time runs out.</summary>
    public static void Repaste(SKCanvas canvas, float remaining)
    {
        var accent = new SKColor(76, 194, 255);
        using var track = Stroke(new SKColor(40, 40, 40), 2.5f);
        canvas.DrawCircle(36, 36, 32, track);
        using var ring = Stroke(accent, 2.5f);
        canvas.DrawArc(new SKRect(4, 4, 68, 68), -90, 360 * remaining, false, ring);

        using var outline = Stroke(new SKColor(230, 230, 230), 2);
        canvas.DrawRoundRect(new SKRect(26, 15, 46, 41), 3.5f, 3.5f, outline);
        using var clip = Fill(new SKColor(230, 230, 230));
        canvas.DrawRoundRect(new SKRect(31, 12, 41, 18), 2, 2, clip);
        using var lines = Stroke(accent, 1.8f);
        canvas.DrawLine(30.5f, 25, 41.5f, 25, lines);
        canvas.DrawLine(30.5f, 30, 41.5f, 30, lines);
        canvas.DrawLine(30.5f, 35, 37.5f, 35, lines);
        DrawFittedText(canvas, "Paste again", new SKRect(8, 45, 64, 60), 11, 9, 1, new SKColor(200, 200, 200), Semibold);
    }

    static readonly SKColor WorkflowAccent = new(168, 85, 247);

    /// <summary>The permanent Workflows key: a little three-step pipeline.</summary>
    public static void WorkflowsButton(SKCanvas canvas)
    {
        using var line = Stroke(new SKColor(120, 120, 120), 2);
        canvas.DrawLine(22, 27, 50, 27, line);
        using var ring = Stroke(new SKColor(215, 215, 215), 2);
        using var hole = Fill(SKColors.Black);
        foreach (var x in new[] { 18f, 36f })
        {
            canvas.DrawCircle(x, 27, 5.5f, hole);
            canvas.DrawCircle(x, 27, 5.5f, ring);
        }
        using var last = Fill(WorkflowAccent);
        canvas.DrawCircle(54, 27, 6.5f, last);
        using var play = Fill(SKColors.White);
        using var tri = new SKPathBuilder();
        tri.MoveTo(52, 23.5f); tri.LineTo(57.5f, 27); tri.LineTo(52, 30.5f); tri.Close();
        using var triPath = tri.Detach();
        canvas.DrawPath(triPath, play);
        DrawFittedText(canvas, "Workflows", new SKRect(5, 44, 67, 60), 12, 9, 1, new SKColor(215, 215, 215), Semibold);
    }

    public static void Workflow(SKCanvas canvas, Workflow workflow)
    {
        var color = ParseColor(workflow.Color);
        using var fill = Fill(new SKColor((byte)(color.Red * 0.2), (byte)(color.Green * 0.2), (byte)(color.Blue * 0.2)));
        canvas.DrawRoundRect(new SKRect(3, 3, 69, 69), 11, 11, fill);
        using var border = Stroke(color.WithAlpha(210), 1.5f);
        canvas.DrawRoundRect(new SKRect(3.75f, 3.75f, 68.25f, 68.25f), 10.5f, 10.5f, border);
        if (DrawIcon(canvas, workflow.Icon, new SKRect(22, 7, 50, 35)))
        {
            // Icon centre stage; agent logo small in the corner; name beneath.
            DrawLogo(canvas, workflow.Agent, new SKRect(7, 7, 19, 19));
            DrawFittedText(canvas, workflow.Name, new SKRect(5, 37, 67, 67), 13, 9, 2, SKColors.White);
            return;
        }
        DrawLogo(canvas, workflow.Agent, new SKRect(8, 8, 24, 24));
        using var play = Fill(color);
        using var tri = new SKPathBuilder();
        tri.MoveTo(56, 11); tri.LineTo(63, 16); tri.LineTo(56, 21); tri.Close();
        using var triPath = tri.Detach();
        canvas.DrawPath(triPath, play);
        DrawFittedText(canvas, workflow.Name, new SKRect(6, 28, 66, 66), 15, 9, 3, SKColors.White);
    }

    public static void NewWorkflow(SKCanvas canvas)
    {
        using var border = Stroke(new SKColor(90, 90, 90), 1.5f);
        border.PathEffect = SKPathEffect.CreateDash([4, 4], 0);
        canvas.DrawRoundRect(new SKRect(4, 4, 68, 68), 10, 10, border);
        using var plus = Stroke(new SKColor(200, 200, 200), 2.5f);
        canvas.DrawLine(36, 17, 36, 35, plus);
        canvas.DrawLine(27, 26, 45, 26, plus);
        DrawFittedText(canvas, "New workflow", new SKRect(6, 42, 66, 64), 11, 9, 2, new SKColor(170, 170, 170), Semibold);
    }

    public static void Launcher(SKCanvas canvas, AgentKind agent)
    {
        using var border = Stroke(new SKColor(60, 60, 60), 1.2f);
        border.PathEffect = SKPathEffect.CreateDash([3, 3], 0);
        canvas.DrawRoundRect(new SKRect(4, 4, 68, 68), 10, 10, border);
        DrawLogo(canvas, agent, new SKRect(24, 11, 48, 35), 150);
        DrawFittedText(canvas, "+ " + agent, new SKRect(6, 42, 66, 62), 12, 9, 1, new SKColor(160, 160, 160), Semibold);
    }

    public static void Enter(SKCanvas canvas, float? press)
    {
        float pulse = press is { } p ? MathF.Sin(MathF.PI * p) : 0;
        if (pulse > 0)
        {
            byte g = (byte)(38 * pulse);
            using var glow = Fill(new SKColor(g, g, g));
            canvas.DrawCircle(36, 36, 22 + 6 * pulse, glow);
        }
        byte shade = (byte)(165 + 90 * pulse);
        using var paint = Stroke(new SKColor(shade, shade, shade), 2);
        float dx = -6 * pulse;
        canvas.DrawLine(47 + dx, 23, 47 + dx, 41, paint);
        canvas.DrawLine(47 + dx, 41, 25 + dx, 41, paint);
        canvas.DrawLine(25 + dx, 41, 32 + dx, 34, paint);
        canvas.DrawLine(25 + dx, 41, 32 + dx, 48, paint);
    }

    static void DrawMicGlyph(SKCanvas canvas, SKColor color, bool filled)
    {
        using var stroke = Stroke(color, 1.6f);
        var body = new SKRect(30, 17, 42, 38);
        if (filled)
        {
            using var fill = Fill(color);
            canvas.DrawRoundRect(body, 6, 6, fill);
        }
        else canvas.DrawRoundRect(body, 6, 6, stroke);
        canvas.DrawArc(new SKRect(25, 22, 47, 44), 0, 180, false, stroke);
        canvas.DrawLine(36, 44, 36, 51, stroke);
        canvas.DrawLine(31, 51, 41, 51, stroke);
    }

    public static void Mic(SKCanvas canvas, DictationState state, float t, float level)
    {
        switch (state)
        {
            case DictationState.Idle:
                DrawMicGlyph(canvas, new SKColor(225, 225, 225), false);
                break;

            case DictationState.Listening:
            {
                float intro = 1 - MathF.Pow(1 - Math.Min(t / 0.35f, 1), 3); // ease-out grow-in
                float p = t % 1.6f / 1.6f;                                    // ripple that expands and fades
                using var ring = Stroke(new SKColor((byte)(Green.Red * (1 - p) * 0.6f), (byte)(Green.Green * (1 - p) * 0.6f),
                                                    (byte)(Green.Blue * (1 - p) * 0.6f)), 1.5f);
                canvas.DrawCircle(36, 36, (26 + 9 * p) * intro, ring);
                using var disc = Fill(Green);
                canvas.DrawCircle(36, 36, (23 + 1.2f * MathF.Sin(t * 3) + 7 * level) * intro, disc);
                DrawMicGlyph(canvas, SKColors.White, true);
                break;
            }

            case DictationState.Finishing:
            {
                DrawMicGlyph(canvas, new SKColor(140, 140, 140), false);
                using var arc = Stroke(Amber, 2.5f);
                canvas.DrawArc(new SKRect(5, 5, 67, 67), t * 420 % 360, 100, false, arc);
                break;
            }

            case DictationState.Cancelled:
            {
                // Grey disc with an X that shrinks and fades away over ~0.8 s.
                float fade = Math.Clamp(1 - t / 0.8f, 0, 1);
                byte g = (byte)(70 * fade);
                using var disc = Fill(new SKColor(g, g, g));
                canvas.DrawCircle(36, 36, 16 + 10 * fade, disc);
                using var x = Stroke(new SKColor(230, 230, 230, (byte)(255 * fade)), 3);
                float r = 5 + 4 * fade;
                canvas.DrawLine(36 - r, 36 - r, 36 + r, 36 + r, x);
                canvas.DrawLine(36 + r, 36 - r, 36 - r, 36 + r, x);
                break;
            }

            case DictationState.Error:
            {
                using var disc = Fill(Red);
                canvas.DrawCircle(36, 36, 27, disc);
                DrawMicGlyph(canvas, SKColors.White, true);
                break;
            }
        }
    }
}
