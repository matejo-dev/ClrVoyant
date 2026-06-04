using System.Diagnostics;

// Test target for child-process auto-attach: prints its PID, spawns a .NET child
// (the SampleApp dll passed as args[0]) and stays alive.
Console.WriteLine($"ParentApp PID={Environment.ProcessId}");

if (args.Length > 0)
{
    var psi = new ProcessStartInfo("dotnet", $"\"{args[0]}\"") { UseShellExecute = false };
    Process.Start(psi);
}

while (true)
    await Task.Delay(1000);
