using System.Drawing;

// A WinForms debug target whose breakpoint is reached THROUGH NATIVE CODE: Form.Show()
// calls the Win32 ShowWindow, which re-enters managed code via the window procedure
// (WM_SHOWWINDOW -> OnLoad). The call stack at the breakpoint therefore crosses native
// frames — the case behind GitHub issue #1. No message loop is needed: Show() delivers
// WM_SHOWWINDOW synchronously. The form is placed off-screen and closed right away.
static class Program
{
    [STAThread]
    static void Main()
    {
        Console.WriteLine($"WinFormsApp PID={Environment.ProcessId}");
        while (true)
        {
            using (var form = new LoadProbeForm())
            {
                form.Show();
                form.Close();
            }
            Thread.Sleep(500);
        }
    }
}

sealed class LoadProbeForm : Form
{
    public LoadProbeForm()
    {
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(-32000, -32000);
    }

    protected override void OnLoad(EventArgs e)
    {
        int loadMarker = 42;   // BREAKPOINT-TARGET: reached from the native window procedure
        GC.KeepAlive(loadMarker);
        base.OnLoad(e);
    }
}
