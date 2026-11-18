namespace Floatboard;

static class Program
{
    [STAThread]
    static void Main()
    {
        using var instance = new Mutex(true, @"Local\Floatboard", out bool first);
        if (!first)
        {
            MessageBox.Show("Floatboard is already open.");
            return;
        }
        ApplicationConfiguration.Initialize();
        Application.Run(new Board());
    }
}
