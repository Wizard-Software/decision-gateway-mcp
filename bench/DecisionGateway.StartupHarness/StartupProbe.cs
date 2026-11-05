using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace DecisionGateway.StartupHarness;

/// <summary>A single run could not be measured: the process failed to start, timed out, or never answered.</summary>
internal sealed class StartupProbeException(string message) : Exception(message);

/// <summary>
/// Reads newline-terminated lines from a byte stream without buffering past a fixed per-line limit.
/// A line is returned as soon as its newline arrives, so the caller can timestamp the arrival.
/// </summary>
internal sealed class BoundedLineReader(Stream stream, int maxLineBytes)
{
    private readonly byte[] _buffer = new byte[8192];
    private readonly ArrayBufferWriter<byte> _line = new();
    private int _start;
    private int _end;

    /// <summary>Returns the next line without its '\n' terminator, or null at end of stream.</summary>
    /// <exception cref="StartupProbeException">The line is longer than the configured limit.</exception>
    public async ValueTask<byte[]?> ReadLineAsync(CancellationToken cancellationToken)
    {
        _line.ResetWrittenCount();
        while (true)
        {
            if (_start == _end)
            {
                var read = await stream.ReadAsync(_buffer.AsMemory(), cancellationToken);
                if (read == 0)
                {
                    return _line.WrittenCount == 0 ? null : _line.WrittenSpan.ToArray();
                }

                _start = 0;
                _end = read;
            }

            var available = _buffer.AsSpan(_start, _end - _start);
            var newline = available.IndexOf((byte)'\n');
            var take = newline < 0 ? available.Length : newline;
            if (_line.WrittenCount + take > maxLineBytes)
            {
                throw new StartupProbeException($"A stdout line exceeded the limit of {maxLineBytes} bytes.");
            }

            _line.Write(available[..take]);
            if (newline >= 0)
            {
                _start += take + 1;
                return _line.WrittenSpan.ToArray();
            }

            _start = _end;
        }
    }
}

/// <summary>Measures one process start: from just before <see cref="Process.Start()"/> to the <c>initialize</c> response.</summary>
internal static class StartupProbe
{
    /// <summary>A constant, well-formed response used to warm up the parsing path and by the warm-up process.</summary>
    internal const string SampleInitializeResponse =
        """{"jsonrpc":"2.0","id":1,"result":{"protocolVersion":"2025-11-25","capabilities":{},"serverInfo":{"name":"warmup","version":"0"}}}""";

    internal const string WarmupNoopArgument = "--warmup-noop";

    private const string InitializeRequest =
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"startup-harness","version":"1.0"}}}""" + "\n";

    private const int MaxLineBytes = 1024 * 1024;
    private const int StderrTailChars = 4096;
    private static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan WarmupTimeout = TimeSpan.FromSeconds(30);

    // The measured process starts from an EMPTY environment on purpose: secrets of the runner must not leak into it.
    // Same allowlist as the host process helper of the contract tests.
    private static readonly string[] AllowedEnvironmentVariables =
    [
        "PATH",
        "HOME",
        "USERPROFILE",
        "DOTNET_ROOT",
        "TMP",
        "TEMP",
        "TMPDIR",
        "SYSTEMROOT",
        "PATHEXT",
    ];

    /// <summary>
    /// Classifies one stdout line. Returns true for the successful response to request id 1, false for any other line
    /// (notifications, logs, other ids, non-JSON), and throws for a JSON-RPC error response to request id 1.
    /// </summary>
    internal static bool TryParseInitializeResponse(ReadOnlySpan<byte> utf8Line)
    {
        try
        {
            var reader = new Utf8JsonReader(utf8Line);
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("id", out var id)
                || id.ValueKind != JsonValueKind.Number
                || !id.TryGetInt32(out var idValue)
                || idValue != 1)
            {
                return false;
            }

            if (root.TryGetProperty("error", out var error))
            {
                var message = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var text)
                    ? text.ToString()
                    : error.ToString();
                throw new StartupProbeException($"The host answered initialize with an error: {message}");
            }

            return root.TryGetProperty("result", out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Warms the harness's own code paths before run 1 without starting the measured binary: parses a constant
    /// response with the production parser, then runs one throw-away process through the exact same probe path.
    /// </summary>
    public static async Task WarmUpAsync(CancellationToken cancellationToken)
    {
        if (!TryParseInitializeResponse(Encoding.UTF8.GetBytes(SampleInitializeResponse)))
        {
            throw new StartupProbeException("The built-in sample response was not recognized as an initialize response.");
        }

        var processPath = Environment.ProcessPath
            ?? throw new StartupProbeException("Environment.ProcessPath is unavailable; cannot run the warm-up process.");

        var arguments = new List<string>();
        if (string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            // Running as "dotnet harness.dll": the host needs the harness assembly as its first argument.
            arguments.Add(typeof(StartupProbe).Assembly.Location);
        }

        arguments.Add(WarmupNoopArgument);

        var options = new HarnessOptions(processPath, arguments, Runs: 2, Rid: "warmup", OutputPath: string.Empty, WarmupTimeout);
        _ = await MeasureOnceAsync(options, cancellationToken);
    }

    /// <summary>Starts the binary, sends <c>initialize</c> and returns the time from just before start to the response line.</summary>
    /// <exception cref="StartupProbeException">The process could not be started, timed out, exited, or answered with an error.</exception>
    public static async Task<TimeSpan> MeasureOnceAsync(HarnessOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(options.Timeout);
        var token = timeoutSource.Token;

        using var process = new Process { StartInfo = CreateStartInfo(options) };
        var stderrTail = new StderrTail();
        var stderrTask = Task.CompletedTask;

        // Registered BEFORE the start timestamp so the measured window stays clean. Some pipe reads ignore
        // cancellation (synchronous anonymous pipes on Windows), so the token must also kill the process tree:
        // that closes stdout and the pending read returns EOF or fails. Killing a not-yet-started process throws
        // InvalidOperationException, which is swallowed (the cancelled token then stops the run anyway).
        var killOnCancel = token.Register(static state => TryKillTree((Process)state!), process);
        try
        {
            var start = Stopwatch.GetTimestamp();
            try
            {
                process.Start();
            }
            catch (Win32Exception exception)
            {
                throw new StartupProbeException($"Could not start '{options.BinaryPath}': {exception.Message}");
            }

            stderrTask = DrainAsync(process.StandardError, stderrTail, token);

            try
            {
                await process.StandardInput.WriteAsync(InitializeRequest.AsMemory(), token);
                await process.StandardInput.FlushAsync(token);
            }
            catch (IOException)
            {
                // The process may already be gone (broken pipe); the read below reports how it ended.
            }

            var reader = new BoundedLineReader(process.StandardOutput.BaseStream, MaxLineBytes);
            var elapsed = TimeSpan.Zero;
            while (true)
            {
                var line = await reader.ReadLineAsync(token);

                // The end marker is taken right after the line arrived, before it is parsed.
                var end = Stopwatch.GetTimestamp();
                if (line is null)
                {
                    // EOF caused by the kill-on-cancel callback is a timeout/deadline, not "exited without answering".
                    token.ThrowIfCancellationRequested();
                    await process.WaitForExitAsync(token);
                    await stderrTask.WaitAsync(token);
                    throw new StartupProbeException(
                        $"'{options.BinaryPath}' exited with code {process.ExitCode} without answering initialize.{stderrTail.Describe()}");
                }

                if (TryParseInitializeResponse(line))
                {
                    elapsed = Stopwatch.GetElapsedTime(start, end);
                    break;
                }
            }

            // A successful run must not be killed by a later cancel.
            killOnCancel.Dispose();
            await ShutDownAsync(process, cancellationToken);
            return elapsed;
        }
        catch (IOException) when (cancellationToken.IsCancellationRequested)
        {
            // The read failed because the deadline killed the process: report the deadline, not an I/O error.
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException
            && timeoutSource.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
            throw new StartupProbeException(
                $"The run timed out after {options.Timeout.TotalSeconds:0.###} s waiting for the initialize response.{stderrTail.Describe()}");
        }
        finally
        {
            killOnCancel.Dispose();
            KillAndWait(process);
            try
            {
                await stderrTask.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
            }
            catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
            {
                // Best effort: a grandchild may still hold the pipe open.
            }
        }
    }

    private static ProcessStartInfo CreateStartInfo(HarnessOptions options)
    {
        var startInfo = new ProcessStartInfo(options.BinaryPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };

        // Arguments only through ArgumentList: no shell and no quoting of a command-line string.
        foreach (var argument in options.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment.Clear();
        foreach (var name in AllowedEnvironmentVariables)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(value))
            {
                startInfo.Environment[name] = value;
            }
        }

        return startInfo;
    }

    // Closing stdin makes the host leave its read loop; the time spent here is not part of the measurement.
    private static async Task ShutDownAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // Already closed by the other side.
        }

        using var graceSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        graceSource.CancelAfter(ShutdownGrace);
        try
        {
            await process.WaitForExitAsync(graceSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Grace period elapsed: the finally block kills the process tree.
        }
    }

    private static void TryKillTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Not started yet, or already exited.
        }
    }

    private static void KillAndWait(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Never started, or exited between the check and the kill.
        }

        try
        {
            process.WaitForExit();
        }
        catch (InvalidOperationException)
        {
            // Never started.
        }
    }

    private static async Task DrainAsync(StreamReader stderr, StderrTail tail, CancellationToken cancellationToken)
    {
        var buffer = new char[1024];
        try
        {
            int read;
            while ((read = await stderr.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
            {
                tail.Append(buffer.AsSpan(0, read));
            }
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // The stream ended abruptly or the run was cancelled; whatever was captured so far is kept.
        }
    }

    /// <summary>Keeps only the last few KB of stderr for error messages while the pipe is always drained.</summary>
    private sealed class StderrTail
    {
        private readonly Lock _gate = new();
        private readonly StringBuilder _text = new();

        public void Append(ReadOnlySpan<char> chunk)
        {
            lock (_gate)
            {
                _text.Append(chunk);
                if (_text.Length > StderrTailChars)
                {
                    _text.Remove(0, _text.Length - StderrTailChars);
                }
            }
        }

        public string Describe()
        {
            lock (_gate)
            {
                return _text.Length == 0 ? string.Empty : $" stderr (tail): {_text.ToString().Trim()}";
            }
        }
    }
}
