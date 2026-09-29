namespace AgentDeck.Core;

/// <summary>Reads a folder's current git branch straight from .git/HEAD (no git process, cheap enough to poll).</summary>
static class GitBranch
{
    /// <summary>The branch name, a short commit hash when detached, or null when the folder isn't in a git repo.</summary>
    public static string? Of(string folder)
    {
        try
        {
            for (var dir = new DirectoryInfo(folder); dir != null; dir = dir.Parent)
            {
                var dotGit = Path.Combine(dir.FullName, ".git");
                string? gitDir = null;
                if (Directory.Exists(dotGit)) gitDir = dotGit;
                else if (File.Exists(dotGit)) // worktree or submodule: "gitdir: <path>"
                {
                    var line = File.ReadAllText(dotGit).Trim();
                    if (line.StartsWith("gitdir:")) gitDir = Path.GetFullPath(line[7..].Trim(), dir.FullName);
                }
                if (gitDir == null) continue;

                var head = File.ReadAllText(Path.Combine(gitDir, "HEAD")).Trim();
                if (head.StartsWith("ref: refs/heads/")) return head[16..];
                if (head.StartsWith("ref: ")) return head[5..];
                return head.Length >= 7 ? head[..7] : head; // detached HEAD
            }
        }
        catch (Exception) { } // missing folder, unreadable HEAD mid-checkout: just show no branch this time
        return null;
    }
}
