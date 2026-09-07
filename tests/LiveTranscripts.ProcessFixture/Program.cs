using System.Diagnostics;

if (args is ["child"])
{
    await Task.Delay(TimeSpan.FromSeconds(5));
    return;
}

if (args is ["parent"])
{
    var startInfo = new ProcessStartInfo(Environment.ProcessPath!)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
    };
    startInfo.ArgumentList.Add("child");
    Process.Start(startInfo)?.Dispose();
    Console.WriteLine("{\"success\":true}");
}