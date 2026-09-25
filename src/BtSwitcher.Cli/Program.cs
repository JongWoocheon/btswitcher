using System.Text;
using System.Text.Json;
using BtSwitcher.Core;
using BtSwitcher.Windows;

Console.OutputEncoding = Encoding.UTF8;
var backend = new WindowsAudioBackend();
try
{
    if (args.Length == 0 || args[0] is "help" or "--help")
    {
        Console.WriteLine("btswitcher list | probe | switch <device-id> | connect <device-id> | disconnect <device-id>\nThe list and probe commands are read-only. Other commands change device connections or the default audio output. Use the exact Id shown by list.");
        return 0;
    }
    if (args[0] == "probe")
    {
        Console.WriteLine(JsonSerializer.Serialize(await backend.ProbeAsync(), new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
    if (args[0] == "list")
    {
        Console.WriteLine(JsonSerializer.Serialize(await backend.SnapshotAsync(default), new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
    if (args.Length != 2 || !Enum.TryParse<OperationKind>(args[0], true, out var kind))
        throw new ArgumentException("Usage: switch|connect|disconnect <device-id> (use the exact Id shown by list)");
    using var lease = WindowsOperationLease.TryAcquire();
    if (lease is null) throw new InvalidOperationException("Another instance is operating on a device. Try again later.");
    var result = await new SwitchCoordinator(backend).ExecuteAsync(kind, args[1], Console.WriteLine);
    Console.WriteLine(result.Message);
    return result.Kind switch { ResultKind.Success => 0, ResultKind.Partial => 2, _ => 1 };
}
catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
