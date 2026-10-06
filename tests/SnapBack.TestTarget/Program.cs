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
        Application.Run(new TargetForm(mode));
    }

    private sealed class TargetForm : Form
    {
        private const int WM_ENDSESSION = 0x0016;
        private readonly string _mode;

        public TargetForm(string mode)
        {
            _mode = mode;
            Text = "SnapBack test target (" + mode + ")";
            Width = 400;
            Height = 200;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason != CloseReason.WindowsShutDown)
            {
                if (_mode == "prompt")
                {
                    e.Cancel = true;
                }
                else if (_mode == "tray")
                {
                    e.Cancel = true;
                    Hide();
                }
            }

            base.OnFormClosing(e);
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);

            // Restart Manager / sign-out: the session (or app) is ending - exit like a real tray app.
            if (m.Msg == WM_ENDSESSION && m.WParam != 0 && _mode != "prompt")
            {
                Application.Exit();
            }
        }
    }
}
