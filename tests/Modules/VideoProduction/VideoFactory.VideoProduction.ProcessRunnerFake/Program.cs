using System.Diagnostics;
using System.Text;
using System.Text.Json;

if (args.Length == 0)
{
    return 2;
}

switch (args[0])
{
    case "success":
        Console.Out.Write("success-output");
        Console.Error.Write("success-diagnostic");
        return 0;

    case "non-zero":
        Console.Error.Write("fake failure");
        return int.Parse(args[1]);

    case "timeout":
        await Task.Delay(int.Parse(args[1]));
        return 0;

    case "large-output":
        await WriteBytesAsync(Console.OpenStandardOutput(), int.Parse(args[1]));
        await WriteBytesAsync(Console.OpenStandardError(), int.Parse(args[1]));
        return 0;

    case "argument-dump":
        await File.WriteAllTextAsync(
            args[1],
            JsonSerializer.Serialize(args.Skip(2).ToArray()));
        return 0;

    case "spawn-child":
        return await SpawnChildAsync(args[1]);

    case "child-loop":
        await Task.Delay(Timeout.InfiniteTimeSpan);
        return 0;

    default:
        return 2;
}

static async Task WriteBytesAsync(Stream stream, int totalBytes)
{
    byte[] buffer = Encoding.UTF8.GetBytes(new string('x', 8192));
    int remaining = totalBytes;

    while (remaining > 0)
    {
        int count = Math.Min(remaining, buffer.Length);
        await stream.WriteAsync(buffer.AsMemory(0, count));
        remaining -= count;
    }

    await stream.FlushAsync();
}

static async Task<int> SpawnChildAsync(string pidFile)
{
    string assemblyPath = typeof(Program).Assembly.Location;
    ProcessStartInfo startInfo = new()
    {
        FileName = Environment.ProcessPath!,
        UseShellExecute = false,
        CreateNoWindow = true
    };
    startInfo.ArgumentList.Add(assemblyPath);
    startInfo.ArgumentList.Add("child-loop");

    using Process child = Process.Start(startInfo)!;
    await File.WriteAllTextAsync(pidFile, child.Id.ToString());
    await child.WaitForExitAsync();
    return 0;
}
