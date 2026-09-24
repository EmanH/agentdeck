using AgentDeck.Core;
using AgentDeck.Dictation;
using SkiaSharp;

namespace AgentDeck.Deck;

/// <summary>
/// Dev aid for docs: `AgentDeck.exe --render-deck &lt;dir&gt;` renders mock Stream Deck screenshots (device frame
/// and all 15 keys, at 2x) using the real key art, without touching the hardware or a running instance.
/// </summary>
static class DeckMock
{
    const int Key = 72, Scale = 2, Gap = 18, Pad = 34;

    public static void RenderAll(string dir)
    {
        Directory.CreateDirectory(dir);
        var agentDeck = new Project { Name = "AgentDeck", Color = "#3b82f6", Icon = "rocket" };
        var website = new Project { Name = "Website", Color = "#22c55e", Icon = "globe-showing-americas" };
        var blue = KeyArt.ParseColor(agentDeck.Color);
        var claude = new Session { Id = 1, ProjectId = "", Agent = AgentKind.Claude, Title = "Fix Login Bug" };
        var codex = new Session { Id = 2, ProjectId = "", Agent = AgentKind.Codex, Summary = "Deploy Script" };
        var grok = new Session { Id = 3, ProjectId = "", Agent = AgentKind.Grok, Summary = "API Tests" };
        string[] moreColors = ["#f59e0b", "#a855f7", "#ec4899"];

        Render(Path.Combine(dir, "deck-main.png"),
        [
            c => KeyArt.Project(c, agentDeck, true, 3),
            c => KeyArt.Session(c, claude, blue, true, 0.9f),
            c => KeyArt.Session(c, codex, blue, false, -1, done: true),
            c => KeyArt.Session(c, grok, blue, false, 0.4f),
            c => KeyArt.Launcher(c, AgentKind.Claude),
            c => KeyArt.Project(c, website, false, 2, hasDone: true),
            c => KeyArt.Launcher(c, AgentKind.Codex),
            c => KeyArt.Launcher(c, AgentKind.Grok),
            _ => { },
            _ => { },
            c => KeyArt.MoreProjects(c, moreColors),
            c => KeyArt.Repaste(c, 0.62f),
            KeyArt.WorkflowsButton,
            c => KeyArt.Enter(c, null),
            c => KeyArt.Mic(c, DictationState.Listening, 1.1f, 0.55f),
        ]);

        Workflow W(string name, AgentKind agent, string color, string icon) => new() { Name = name, Agent = agent, Color = color, Icon = icon };
        Render(Path.Combine(dir, "deck-workflows.png"),
        [
            c => KeyArt.Project(c, agentDeck, true, 3),
            c => KeyArt.Workflow(c, W("Review Open PRs", AgentKind.Claude, "#a855f7", "magnifying-glass-tilted-left")),
            c => KeyArt.Workflow(c, W("Update Deps", AgentKind.Codex, "#14b8a6", "package")),
            c => KeyArt.Workflow(c, W("Ship It", AgentKind.Grok, "#f97316", "rocket")),
            c => KeyArt.Workflow(c, W("Write Tests", AgentKind.Claude, "#22c55e", "test-tube")),
            c => KeyArt.Project(c, website, false, 2),
            c => KeyArt.Workflow(c, W("Fix Lint", AgentKind.Codex, "#eab308", "broom")),
            c => KeyArt.Workflow(c, W("Security Audit", AgentKind.Claude, "#ef4444", "shield")),
            KeyArt.NewWorkflow,
            _ => { },
            c => KeyArt.MoreProjects(c, moreColors),
            _ => { },
            KeyArt.Back,
            c => KeyArt.Enter(c, null),
            c => KeyArt.Mic(c, DictationState.Idle, 0, 0),
        ]);

        Render(Path.Combine(dir, "deck-hold-to-close.png"),
        [
            c => KeyArt.Project(c, agentDeck, true, 3),
            c => KeyArt.Session(c, claude, blue, true, -1),
            c => { KeyArt.Session(c, codex, blue, false, -1); KeyArt.HoldRing(c, 0.7f); },
            c => KeyArt.Session(c, grok, blue, false, -1, done: true),
            c => KeyArt.Launcher(c, AgentKind.Claude),
            c => KeyArt.Project(c, website, false, 2),
            c => KeyArt.Launcher(c, AgentKind.Codex),
            c => KeyArt.Launcher(c, AgentKind.Grok),
            _ => { },
            _ => { },
            c => KeyArt.MoreProjects(c, moreColors),
            _ => { },
            KeyArt.WorkflowsButton,
            c => KeyArt.Enter(c, 0.4f),
            c => KeyArt.Mic(c, DictationState.Finishing, 0.3f, 0),
        ]);
    }

    /// <summary>Draw a 5x3 Stream Deck: brushed dark body, recessed key wells, each key's art.</summary>
    static void Render(string path, Action<SKCanvas>[] keys)
    {
        int cols = 5, rows = 3;
        int w = (Pad * 2 + cols * Key + (cols - 1) * Gap) * Scale;
        int h = (Pad * 2 + rows * Key + (rows - 1) * Gap) * Scale;
        using var surface = SKSurface.Create(new SKImageInfo(w + 80, h + 80, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        canvas.Translate(40, 40);

        // Soft drop shadow, then the body with a subtle vertical gradient and top highlight.
        var body = new SKRoundRect(new SKRect(0, 0, w, h), 26 * Scale);
        using (var shadow = new SKPaint { Color = new SKColor(0, 0, 0, 150), IsAntialias = true, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 22) })
        {
            canvas.Save();
            canvas.Translate(0, 14);
            canvas.DrawRoundRect(body, shadow);
            canvas.Restore();
        }
        using (var fill = new SKPaint { IsAntialias = true, Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, h),
                   [new SKColor(46, 46, 50), new SKColor(22, 22, 25)], SKShaderTileMode.Clamp) })
            canvas.DrawRoundRect(body, fill);
        using (var rim = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2, Color = new SKColor(255, 255, 255, 28) })
            canvas.DrawRoundRect(body, rim);

        for (int i = 0; i < keys.Length && i < cols * rows; i++)
        {
            float x = (Pad + i % cols * (Key + Gap)) * Scale, y = (Pad + i / cols * (Key + Gap)) * Scale;
            var well = new SKRect(x - 5 * Scale, y - 5 * Scale, x + (Key + 5) * Scale, y + (Key + 5) * Scale);
            using (var wellPaint = new SKPaint { IsAntialias = true, Color = new SKColor(8, 8, 10) })
                canvas.DrawRoundRect(well, 12 * Scale, 12 * Scale, wellPaint);

            using var key = new SKBitmap(Key * Scale, Key * Scale);
            using (var kc = new SKCanvas(key))
            {
                kc.Clear(SKColors.Black);
                kc.Scale(Scale);
                keys[i](kc);
            }
            canvas.Save();
            canvas.ClipRoundRect(new SKRoundRect(new SKRect(x, y, x + Key * Scale, y + Key * Scale), 9 * Scale), antialias: true);
            using var image = SKImage.FromBitmap(key);
            canvas.DrawImage(image, x, y);
            canvas.Restore();
        }

        using var snapshot = surface.Snapshot();
        using var data = snapshot.Encode(SKEncodedImageFormat.Png, 100);
        using var file = File.Create(path);
        data.SaveTo(file);
    }
}
