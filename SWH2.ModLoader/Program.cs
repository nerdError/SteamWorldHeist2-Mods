namespace SWH2.ModLoader;

internal static class Program
{
    /// <summary>
    /// SWH2ModLoader.exe [--game DIR] [--apply]
    ///   --game DIR  open this game folder (default: the one the loader sits in, else the last one / auto-detect)
    ///   --apply     click "Apply to game" once the window is up (used by build.cmd after deploying new mod versions)
    /// </summary>
    [STAThread]
    static void Main(string[] args)
    {
        string? game = null;
        bool apply = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--apply") apply = true;
            else if (args[i] == "--game" && i + 1 < args.Length) game = args[++i];
        }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm(game, apply));
    }
}
