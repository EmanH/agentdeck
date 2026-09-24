using System.Collections;

namespace AgentDeck;

static class Log
{
    public static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentDeck");
    static readonly string FilePath = Path.Combine(Dir, "agentdeck.log");
    static readonly object Gate = new();

    static Log()
    {
        Directory.CreateDirectory(Dir);
        if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 1_000_000) File.Delete(FilePath);
    }

    public static void Info(string message)
    {
        lock (Gate)
        {
            try { File.AppendAllText(FilePath, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}"); }
            catch (IOException) { }
        }
    }

    public static void Error(string what, Exception ex) => Info($"ERROR {what}: {ex}");
}

static class Env
{
    /// <summary>
    /// Replace this process's environment with a clean login environment (what Explorer or a fresh Windows
    /// Terminal gets), which every terminal inherits. Whatever launched us - e.g. a shell inside another
    /// Claude Code session, whose CLAUDE_CODE_* markers make child Claude sessions misbehave - must not
    /// leak in, and variables added since that shell started (API keys, PATH) must be picked up.
    /// </summary>
    public static void ResetToLoginEnvironment()
    {
        var clean = LoginEnvironment();
        if (clean == null)
        {
            Log.Info("CreateEnvironmentBlock failed; refreshing from the registry instead.");
            RefreshFromRegistry();
            return;
        }
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            if (!clean.ContainsKey((string)e.Key)) Environment.SetEnvironmentVariable((string)e.Key, null);
        foreach (var (name, value) in clean) Environment.SetEnvironmentVariable(name, value);
        Environment.SetEnvironmentVariable("COLORTERM", "truecolor");
        Environment.SetEnvironmentVariable("TERM_PROGRAM", "AgentDeck");
    }

    static Dictionary<string, string>? LoginEnvironment()
    {
        if (!OpenProcessToken(System.Diagnostics.Process.GetCurrentProcess().Handle, 0x0008 /* TOKEN_QUERY */, out var token))
            return null;
        try
        {
            if (!CreateEnvironmentBlock(out var block, token, false)) return null;
            try
            {
                var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var ptr = block;
                while (true)
                {
                    var entry = System.Runtime.InteropServices.Marshal.PtrToStringUni(ptr)!;
                    if (entry.Length == 0) break;
                    int eq = entry.IndexOf('=', 1); // names like "=C:" start with '='
                    if (eq > 0) vars[entry[..eq]] = entry[(eq + 1)..];
                    ptr += (entry.Length + 1) * 2;
                }
                return vars;
            }
            finally { DestroyEnvironmentBlock(block); }
        }
        finally { CloseHandle(token); }
    }

    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
    static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [System.Runtime.InteropServices.DllImport("userenv.dll", SetLastError = true)]
    static extern bool CreateEnvironmentBlock(out IntPtr block, IntPtr token, bool inherit);
    [System.Runtime.InteropServices.DllImport("userenv.dll")]
    static extern bool DestroyEnvironmentBlock(IntPtr block);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);

    /// <summary>Fallback: overlay registry variables onto the inherited environment.</summary>
    static void RefreshFromRegistry()
    {
        var machine = Environment.GetEnvironmentVariables(EnvironmentVariableTarget.Machine);
        var user = Environment.GetEnvironmentVariables(EnvironmentVariableTarget.User);
        foreach (var scope in new[] { machine, user })
            foreach (DictionaryEntry e in scope)
            {
                var name = (string)e.Key;
                if (name.Equals("Path", StringComparison.OrdinalIgnoreCase)) continue;
                Environment.SetEnvironmentVariable(name, Environment.ExpandEnvironmentVariables((string)e.Value!));
            }
        var path = string.Join(';', new[] { machine["Path"], user["Path"] }
            .OfType<string>().Where(p => p.Length > 0).Select(Environment.ExpandEnvironmentVariables));
        if (path.Length > 0) Environment.SetEnvironmentVariable("PATH", path);
        Environment.SetEnvironmentVariable("COLORTERM", "truecolor");
        Environment.SetEnvironmentVariable("TERM_PROGRAM", "AgentDeck");
    }

    public static string? Get(string name)
    {
        var value = Environment.GetEnvironmentVariable(name)?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }
}
