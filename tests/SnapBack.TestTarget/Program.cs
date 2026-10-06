namespace SnapBack.TestTarget;

/// <summary>
/// Modes:
///   normal  - closes when asked;
///   prompt  - refuses to close (like an app showing "save changes?");
///   tray    - hides on close, exits on session end (like Discord/Telegram in the tray).
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var mode = args.Length > 0 ? args[0] : "normal";
        ApplicationConfiguration.Initialize();
        var form = new Form { Text = "SnapBack test target (" + mode + ")", Width = 400, Height = 200 };
        form.FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.WindowsShutDown)
            {
                return;
            }

            if (mode == "prompt")
            {
                e.Cancel = true;
            }
            else if (mode == "tray")
            {
                e.Cancel = true;
                form.Hide();
            }
        };

        Application.Run(form);
    }
}
