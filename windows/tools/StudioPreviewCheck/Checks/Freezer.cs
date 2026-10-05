using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace TinyClips.Tools.StudioPreviewCheck.Checks;

/// <summary>
/// Stops this whole process for some tens of milliseconds, again and again: every thread in it,
/// the players' own among them, as when the PC has no time for it at all. That cannot be done
/// from inside: a thread that suspends the others waits for ever when one of them was stopped
/// in the middle of something the first one needs. So a second copy of this tool is started,
/// and all it does is suspend this process, wait, and let it go on.
/// </summary>
/// <remarks>
/// The second copy ends when this process does, and it lets the process go on before it ends
/// for any reason of its own. Nothing else on the PC is touched.
/// </remarks>
internal sealed partial class Freezer : IDisposable
{
    /// <summary>The first argument of the copy that does the freezing. Not for people.</summary>
    public const string Argument = "--freeze-process";

    private const uint ProcessSuspendResume = 0x0800;
    private const uint Synchronize = 0x00100000;
    private const uint WaitTimeout = 0x00000102;

    private readonly List<(long From, long To)> _stalls = [];
    private readonly Process _child;

    /// <param name="shortest">The shortest time the process is stopped for, in milliseconds.</param>
    /// <param name="longest">The longest.</param>
    /// <param name="shortestGap">The shortest time between two stops.</param>
    /// <param name="longestGap">The longest.</param>
    public Freezer(int shortest, int longest, int shortestGap, int longestGap, int seed = 20261004)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("The tool does not know where its own file is."))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardInput = true,
        };
        var c = CultureInfo.InvariantCulture;
        foreach (var argument in new[] { Argument, Environment.ProcessId.ToString(c), shortest.ToString(c), longest.ToString(c), shortestGap.ToString(c), longestGap.ToString(c), seed.ToString(c) })
        {
            info.ArgumentList.Add(argument);
        }

        _child = Process.Start(info) ?? throw new InvalidOperationException("The copy of the tool that stops this process did not start.");
        _child.OutputDataReceived += (_, e) =>
        {
            // One line for each stop: when it began and ended, on the clock both processes share.
            var parts = e.Data?.Split(' ');
            if (parts is { Length: 2 }
                && long.TryParse(parts[0], NumberStyles.Integer, c, out var from)
                && long.TryParse(parts[1], NumberStyles.Integer, c, out var to))
            {
                lock (_stalls)
                {
                    _stalls.Add((from, to));
                }
            }
        };
        _child.BeginOutputReadLine();
    }

    /// <summary>The stops so far: when each began and ended, as Stopwatch timestamps.</summary>
    public (long From, long To)[] Stalls
    {
        get
        {
            lock (_stalls)
            {
                return [.. _stalls];
            }
        }
    }

    /// <summary>Stopping begins when this is called, and pauses with <see cref="Rest"/>.</summary>
    public void Begin() => Tell("go");

    public void Rest()
    {
        Tell("rest");

        // A stop that had begun is over by then.
        Thread.Sleep(200);
    }

    /// <summary>
    /// Stops the process once, as soon as the second copy has read the word, for so many
    /// milliseconds: for the experiment that aims a stop at a moment. Whether stopping at random
    /// has begun or not makes no difference to it.
    /// </summary>
    public void Now(int milliseconds) => Tell(string.Create(CultureInfo.InvariantCulture, $"now {milliseconds}"));

    public string Describe()
    {
        var stalls = Stalls;
        return stalls.Length == 0
            ? "the process was never stopped"
            : string.Create(CultureInfo.InvariantCulture, $"the whole process was stopped {stalls.Length} times, for {stalls.Min(Length):0} to {stalls.Max(Length):0} ms, {stalls.Sum(Length):0} ms in all");

        static double Length((long From, long To) stall) => Stopwatch.GetElapsedTime(stall.From, stall.To).TotalMilliseconds;
    }

    public void Dispose()
    {
        try
        {
            Tell("end");
            if (!_child.WaitForExit(3000))
            {
                _child.Kill();
                _child.WaitForExit(3000);
            }
        }
        catch (Exception)
        {
            // It ends with this process in any case.
        }

        _child.Dispose();
    }

    private void Tell(string word)
    {
        try
        {
            _child.StandardInput.WriteLine(word);
            _child.StandardInput.Flush();
        }
        catch (Exception)
        {
            // The copy has gone; then nothing is stopped any more.
        }
    }

    /// <summary>What the second copy does. Returns its exit code.</summary>
    public static int Run(string[] args)
    {
        var c = CultureInfo.InvariantCulture;
        if (args.Length != 7
            || !int.TryParse(args[1], NumberStyles.Integer, c, out var processId)
            || !int.TryParse(args[2], NumberStyles.Integer, c, out var shortest)
            || !int.TryParse(args[3], NumberStyles.Integer, c, out var longest)
            || !int.TryParse(args[4], NumberStyles.Integer, c, out var shortestGap)
            || !int.TryParse(args[5], NumberStyles.Integer, c, out var longestGap)
            || !int.TryParse(args[6], NumberStyles.Integer, c, out var seed))
        {
            return 2;
        }

        // Never longer than this, whatever was asked for: a process that is stopped can do nothing about it.
        longest = Math.Clamp(longest, 1, 500);
        shortest = Math.Clamp(shortest, 1, longest);
        shortestGap = Math.Max(20, shortestGap);
        longestGap = Math.Max(shortestGap, longestGap);

        var process = OpenProcess(ProcessSuspendResume | Synchronize, false, processId);
        if (process == 0)
        {
            return 3;
        }

        // One stop at a time, whoever asks for it.
        var stopping = new object();
        int Stop(int hold)
        {
            lock (stopping)
            {
                var from = Stopwatch.GetTimestamp();
                if (NtSuspendProcess(process) != 0)
                {
                    return 4;
                }

                long to;
                try
                {
                    while (Stopwatch.GetElapsedTime(from).TotalMilliseconds < hold - 2)
                    {
                        Thread.Sleep(1);
                    }

                    while (Stopwatch.GetElapsedTime(from).TotalMilliseconds < hold)
                    {
                        Thread.SpinWait(50);
                    }
                }
                finally
                {
                    _ = NtResumeProcess(process);
                    to = Stopwatch.GetTimestamp();
                }

                Console.Out.WriteLine(string.Create(c, $"{from} {to}"));
                Console.Out.Flush();
                return 0;
            }
        }

        // What the process says: go, rest, now <milliseconds>, end. When it says nothing more, it has gone.
        var going = false;
        var ended = false;
        var listener = new Thread(() =>
        {
            try
            {
                while (Console.In.ReadLine() is { } line)
                {
                    switch (line.Trim())
                    {
                        case "go":
                            Volatile.Write(ref going, true);
                            break;
                        case "rest":
                            Volatile.Write(ref going, false);
                            break;
                        case var word when word.StartsWith("now ", StringComparison.Ordinal) && int.TryParse(word.AsSpan(4), NumberStyles.Integer, c, out var once):
                            if (Stop(Math.Clamp(once, 1, 500)) != 0)
                            {
                                Volatile.Write(ref ended, true);
                                return;
                            }

                            break;
                        default:
                            Volatile.Write(ref ended, true);
                            return;
                    }
                }
            }
            catch (Exception)
            {
                // Taken for the end.
            }

            Volatile.Write(ref ended, true);
        })
        {
            IsBackground = true,
        };
        listener.Start();

        var random = new Random(seed);
        try
        {
            while (!Volatile.Read(ref ended))
            {
                // Signalled when the process has ended.
                if (WaitForSingleObject(process, (uint)random.Next(shortestGap, longestGap + 1)) != WaitTimeout)
                {
                    return 0;
                }

                if (!Volatile.Read(ref going) || Volatile.Read(ref ended))
                {
                    continue;
                }

                if (Stop(random.Next(shortest, longest + 1)) != 0)
                {
                    return 4;
                }
            }

            return 0;
        }
        finally
        {
            _ = CloseHandle(process);
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);

    [LibraryImport("kernel32.dll")]
    private static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("ntdll.dll")]
    private static partial int NtSuspendProcess(nint process);

    [LibraryImport("ntdll.dll")]
    private static partial int NtResumeProcess(nint process);
}
