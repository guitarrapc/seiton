namespace Seiton.Cli;

/// <summary>
/// Writes verbose diagnostic output to stderr when verbose mode is enabled.
/// All output uses a <c>verbose: </c> prefix for grep-ability.
/// Use <see cref="Create"/> to obtain an instance; when verbose is disabled,
/// all methods are no-ops with zero formatting overhead.
/// </summary>
internal sealed class VerboseLogger
{
    /// <summary>A no-op logger that produces no output.</summary>
    public static readonly VerboseLogger Null = new(null, null, VerboseLevel.Off);

    private readonly TextWriter? _writer;
    private readonly TimeProvider? _timeProvider;
    private readonly VerboseLevel _level;

    private VerboseLogger(TextWriter? writer, TimeProvider? timeProvider, VerboseLevel level)
    {
        _writer = writer;
        _timeProvider = timeProvider;
        _level = level;
    }

    /// <summary>Gets whether any verbose logging is active.</summary>
    public bool IsEnabled => _writer is not null;

    /// <summary>Gets whether per-file progress lines should be emitted.</summary>
    public bool LogFileProgress => _level >= VerboseLevel.Files;

    /// <summary>
    /// Creates a <see cref="VerboseLogger"/> that writes to <paramref name="stderr"/>
    /// when <paramref name="verbose"/> is <c>true</c> (summary level), or a no-op logger otherwise.
    /// </summary>
    public static VerboseLogger Create(bool verbose, TextWriter stderr)
        => Create(verbose ? VerboseLevel.Summary : VerboseLevel.Off, stderr);

    /// <summary>
    /// Creates a <see cref="VerboseLogger"/> with an explicit <see cref="TimeProvider"/> (for testing).
    /// </summary>
    public static VerboseLogger Create(bool verbose, TextWriter stderr, TimeProvider timeProvider)
        => Create(verbose ? VerboseLevel.Summary : VerboseLevel.Off, stderr, timeProvider);

    /// <summary>
    /// Creates a <see cref="VerboseLogger"/> for the requested <paramref name="level"/>.
    /// </summary>
    public static VerboseLogger Create(VerboseLevel level, TextWriter stderr)
        => level == VerboseLevel.Off ? Null : new VerboseLogger(stderr, TimeProvider.System, level);

    /// <summary>
    /// Creates a <see cref="VerboseLogger"/> with an explicit <see cref="TimeProvider"/> (for testing).
    /// </summary>
    public static VerboseLogger Create(VerboseLevel level, TextWriter stderr, TimeProvider timeProvider)
        => level == VerboseLevel.Off ? Null : new VerboseLogger(stderr, timeProvider, level);

    /// <summary>
    /// Returns a timestamp for measuring elapsed time.
    /// Returns 0 when verbose is disabled, so callers may invoke this unguarded
    /// and pass the result to <see cref="GetElapsedTime(long)"/>.
    /// </summary>
    public long GetTimestamp() => _timeProvider?.GetTimestamp() ?? 0;

    /// <summary>
    /// Returns the elapsed time since <paramref name="startTimestamp"/>.
    /// Returns <see cref="TimeSpan.Zero"/> when verbose is disabled.
    /// </summary>
    public TimeSpan GetElapsedTime(long startTimestamp) => _timeProvider?.GetElapsedTime(startTimestamp) ?? TimeSpan.Zero;

    /// <summary>
    /// Writes <c>verbose: &lt;category&gt;: &lt;message&gt;</c>.
    /// Callers sharing a logger across threads must provide a thread-safe <see cref="TextWriter"/>.
    /// </summary>
    public void Log(string category, string message)
    {
        _writer?.WriteLine($"verbose: {category}: {message}");
    }

    /// <summary>
    /// Writes <c>verbose: &lt;message&gt;</c>.
    /// Callers sharing a logger across threads must provide a thread-safe <see cref="TextWriter"/>.
    /// </summary>
    public void Log(string message)
    {
        _writer?.WriteLine($"verbose: {message}");
    }

    /// <summary>
    /// Writes <c>verbose: &lt;filePath&gt;: &lt;message&gt;</c>.
    /// Callers sharing a logger across threads must provide a thread-safe <see cref="TextWriter"/>.
    /// </summary>
    public void LogFile(string filePath, string message)
    {
        _writer?.WriteLine($"verbose: {filePath}: {message}");
    }
}
