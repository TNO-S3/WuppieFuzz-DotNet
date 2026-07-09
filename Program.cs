// WuppieFuzz .NET Coverage Agent
//
// This is the .NET-specific implementation of the WuppieFuzz Cobertura-over-HTTP
// coverage protocol.  The same HTTP contract can be implemented by agents for
// other languages; the Rust client (src/coverage_clients/cobertura.rs) is
// language-agnostic.
//
// Protocol (used by CoberturaCoverageClient):
//   GET /coverage[?reset=true]  → Cobertura XML snapshot of current coverage
//   GET /report                 → HTML report as a ZIP archive (optional endpoint;
//                                  falls back to raw Cobertura XML if reportgenerator
//                                  is not installed)
//   GET /health                 → "OK"
//   GET /shutdown               → graceful shutdown
//
// .NET-specific implementation detail:
//   This agent wraps Microsoft's `dotnet-coverage` tool.  The target application
//   is started with `dotnet-coverage connect <session-id> <app-command>`, which
//   attaches coverage collection via named pipes (no source changes required).
//   On each /coverage request the agent takes a binary snapshot, converts it to
//   Cobertura XML, and returns the XML.  Snapshots are also accumulated for the
//   final /report.
//
// Performance notes:
//   /coverage is called on every fuzzing iteration, so it is on the fuzzer's
//   hot path.
//   - Folding each snapshot into `accumulated.coverage` is deferred to a
//     single background worker thread instead of running synchronously on
//     every /coverage call; the HTTP response is sent as soon as the
//     snapshot + cobertura-conversion steps are done. /report drains this
//     queue before building the final report so it never observes a stale
//     accumulation.
//   - Binary-to-Cobertura/binary-to-binary conversion ("merge") uses
//     Microsoft.CodeCoverage.Core's CoverageFileUtilityV2 API in-process
//     (see CoverageMerger below) instead of spawning `dotnet-coverage merge`
//     as a subprocess: that CLI command re-builds a large dependency
//     injection graph, sets up file logging, and parses command-line
//     arguments on every invocation, which is measurably much slower than
//     calling the underlying conversion routine directly. `snapshot` still
//     has to go through the CLI, since triggering it requires an internal,
//     non-public IPC client to talk to the running
//     `dotnet-coverage collect --server-mode` process.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.RegularExpressions;

// --- Parse CLI arguments ---
string sessionId = "wuppiefuzz";
int port = 6302;
for (int i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--session-id") sessionId = args[++i];
    else if (args[i] == "--port") port = int.Parse(args[++i]);
}

// Validate session ID to prevent command injection
if (!Regex.IsMatch(sessionId, @"^[a-zA-Z0-9_-]+$"))
    throw new ArgumentException("--session-id must be alphanumeric (hyphens and underscores allowed)");

// --- Temp directory for accumulated coverage ---
string tempDir = Path.Combine(Path.GetTempPath(), $"wuppiefuzz-dotnet-{sessionId}");
Directory.CreateDirectory(tempDir);

// --- Background accumulation worker ---
// Snapshots are merged into accumulated.coverage off the /coverage hot path
// (see performance note above). The queue is processed by a single thread so
// merges never run concurrently against accumulated.coverage.
var accumulateQueue = new BlockingCollection<string>();
int pendingAccumulations = 0;
var accumulatorThread = new Thread(() =>
{
    foreach (string snapFile in accumulateQueue.GetConsumingEnumerable())
    {
        try
        {
            AccumulateCoverage(snapFile, tempDir);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: background accumulation failed: {ex.Message}");
        }
        finally
        {
            if (File.Exists(snapFile)) File.Delete(snapFile);
            Interlocked.Decrement(ref pendingAccumulations);
        }
    }
})
{ IsBackground = true, Name = "wuppiefuzz-accumulator" };
accumulatorThread.Start();

// Hands a snapshot file off to the background worker. Ownership of the file
// transfers to the worker, which deletes it once merged.
void EnqueueAccumulate(string snapFile)
{
    Interlocked.Increment(ref pendingAccumulations);
    accumulateQueue.Add(snapFile);
}

// Blocks (with a timeout) until all previously enqueued snapshots have been
// merged into accumulated.coverage. Used before building a report so it
// reflects the latest coverage rather than a stale accumulation.
void WaitForAccumulationQueueDrained(int timeoutMs = 30_000)
{
    var sw = Stopwatch.StartNew();
    while (Volatile.Read(ref pendingAccumulations) > 0 && sw.ElapsedMilliseconds < timeoutMs)
        Thread.Sleep(20);
    if (Volatile.Read(ref pendingAccumulations) > 0)
        Console.Error.WriteLine("Warning: timed out waiting for background accumulation to drain");
}

// --- Start dotnet-coverage in server mode ---
Console.WriteLine($"Starting dotnet-coverage collect --server-mode --session-id {sessionId}");
Process collectProc = StartBackground("dotnet-coverage", $"collect --server-mode --session-id {sessionId}");

// Give the dotnet-coverage server time to initialise its named-pipe listener
Thread.Sleep(2000);

// --- HTTP server ---
var listener = new HttpListener();
// Use http://+:{port}/ so the agent can be reached from other machines
listener.Prefixes.Add($"http://+:{port}/");
listener.Start();
Console.WriteLine($"WuppieFuzz .NET coverage agent listening on port {port}");
Console.WriteLine($"Session ID : {sessionId}");
Console.WriteLine($"Run target : dotnet-coverage connect {sessionId} <your-app-command>");

while (true)
{
    HttpListenerContext ctx;
    try { ctx = listener.GetContext(); }
    catch (HttpListenerException) { break; }

    var req = ctx.Request;
    var res = ctx.Response;
    try
    {
        string path = req.Url?.AbsolutePath ?? "/";
        bool reset = req.QueryString["reset"] == "true";

        if (path == "/health")
        {
            WriteText(res, 200, "OK");
        }
        else if (path == "/coverage")
        {
            string xml = GetCoverageXml(sessionId, tempDir, reset, EnqueueAccumulate);
            WriteText(res, 200, xml, "application/xml");
        }
        else if (path == "/report")
        {
            // Ensure all queued snapshots are folded into accumulated.coverage
            // before building the report, so it isn't missing recent hits.
            WaitForAccumulationQueueDrained();
            byte[]? zip = GenerateReport(sessionId, tempDir);
            if (zip is not null)
            {
                res.StatusCode = 200;
                res.ContentType = "application/zip";
                res.ContentLength64 = zip.Length;
                res.OutputStream.Write(zip);
                res.OutputStream.Close();
            }
            else
            {
                // Fallback: reportgenerator not installed, return cobertura XML
                // (queue already drained above)
                string xml = GetAccumulatedCoberturaXml(tempDir);
                WriteText(res, 200, xml, "application/xml");
            }
        }
        else if (path == "/shutdown")
        {
            WriteText(res, 200, "Shutting down");
            listener.Stop();
            break;
        }
        else
        {
            WriteText(res, 404, "Not found");
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Request error: {ex.Message}");
        try { WriteText(res, 500, ex.Message); } catch { }
    }
}

// --- Drain background accumulation, then shut down dotnet-coverage ---
WaitForAccumulationQueueDrained();
accumulateQueue.CompleteAdding();
accumulatorThread.Join(5000);
SnapshotClient.DisposeAll();
try { RunProcess("dotnet-coverage", $"shutdown {sessionId}"); } catch { }
collectProc.WaitForExit(5000);
Console.WriteLine("Agent stopped.");

// ============================================================
// Helpers
// ============================================================

/// Snapshot current coverage, optionally reset, convert to cobertura XML.
/// Hands the snapshot off to the background accumulation queue instead of
/// merging it into accumulated.coverage synchronously, keeping the expensive
/// `dotnet-coverage merge` call off the fuzzer's hot path.
static string GetCoverageXml(string sessionId, string tempDir, bool reset, Action<string> enqueueAccumulate)
{
    string guid = Guid.NewGuid().ToString("N");
    string snapFile = Path.Combine(tempDir, $"snap-{guid}.coverage");
    string xmlFile = Path.Combine(tempDir, $"snap-{guid}.xml");
    bool handedOff = false;
    try
    {
        bool snapped = SnapshotClient.TrySnapshot(sessionId, snapFile, reset);
        if (!snapped)
        {
            string resetFlag = reset ? "--reset" : "";
            int rc = RunProcess("dotnet-coverage", $"snapshot {sessionId} {resetFlag} -o \"{snapFile}\"");
            snapped = rc == 0 && File.Exists(snapFile);
        }
        if (!snapped)
        {
            Console.Error.WriteLine("dotnet-coverage snapshot returned no output");
            return "<coverage version=\"1\"><packages/></coverage>";
        }

        // Convert binary .coverage -> cobertura XML for the Rust client
        if (!CoverageMerger.TryMerge(xmlFile, new[] { snapFile }, MergeKind.Cobertura))
            RunProcess("dotnet-coverage", $"merge \"{snapFile}\" -f cobertura -o \"{xmlFile}\"");
        string xml = File.Exists(xmlFile)
            ? File.ReadAllText(xmlFile)
            : "<coverage version=\"1\"><packages/></coverage>";

        // Hand snapshot off to the background worker; it deletes the file
        // once merged into accumulated.coverage (used for /report).
        enqueueAccumulate(snapFile);
        handedOff = true;

        return xml;
    }
    finally
    {
        if (!handedOff && File.Exists(snapFile)) File.Delete(snapFile);
        if (File.Exists(xmlFile)) File.Delete(xmlFile);
    }
}

/// Merges snapFile into accumulated.coverage, creating it if absent.
static void AccumulateCoverage(string snapFile, string tempDir)
{
    string accFile = Path.Combine(tempDir, "accumulated.coverage");
    string tmpAcc = accFile + ".tmp";
    try
    {
        if (!File.Exists(accFile))
        {
            File.Copy(snapFile, accFile);
        }
        else
        {
            bool merged = CoverageMerger.TryMerge(tmpAcc, new[] { accFile, snapFile }, MergeKind.Coverage);
            if (!merged)
            {
                int rc = RunProcess("dotnet-coverage",
                    $"merge \"{accFile}\" \"{snapFile}\" -f coverage -o \"{tmpAcc}\"");
                merged = rc == 0 && File.Exists(tmpAcc);
            }
            if (merged) File.Move(tmpAcc, accFile, overwrite: true);
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Warning: failed to accumulate coverage: {ex.Message}");
    }
    finally
    {
        if (File.Exists(tmpAcc)) File.Delete(tmpAcc);
    }
}

/// Converts accumulated.coverage to cobertura XML and returns it.
static string GetAccumulatedCoberturaXml(string tempDir)
{
    string accFile = Path.Combine(tempDir, "accumulated.coverage");
    if (!File.Exists(accFile))
        return "<coverage version=\"1\"><packages/></coverage>";

    string xmlFile = Path.Combine(tempDir, "accumulated.cobertura.xml");
    if (!CoverageMerger.TryMerge(xmlFile, new[] { accFile }, MergeKind.Cobertura))
        RunProcess("dotnet-coverage", $"merge \"{accFile}\" -f cobertura -o \"{xmlFile}\"");
    if (!File.Exists(xmlFile))
        return "<coverage version=\"1\"><packages/></coverage>";
    string xml = File.ReadAllText(xmlFile);
    File.Delete(xmlFile);
    return xml;
}

/// Generates an HTML report from accumulated coverage, zips it, returns the bytes.
/// Returns null if reportgenerator is not installed or the report directory is empty.
static byte[]? GenerateReport(string sessionId, string tempDir)
{
    string accFile = Path.Combine(tempDir, "accumulated.coverage");
    string xmlFile = Path.Combine(tempDir, "report.cobertura.xml");
    string reportDir = Path.Combine(tempDir, "html");
    string zipFile = Path.Combine(tempDir, "report.zip");
    try
    {
        // If no accumulated coverage yet, take a final snapshot
        if (!File.Exists(accFile))
        {
            string snapFile = Path.Combine(tempDir, $"final-{Guid.NewGuid():N}.coverage");
            if (!SnapshotClient.TrySnapshot(sessionId, snapFile, reset: false))
                RunProcess("dotnet-coverage", $"snapshot {sessionId} -o \"{snapFile}\"");
            if (File.Exists(snapFile))
                File.Move(snapFile, accFile, overwrite: true);
            else
                return null;
        }

        // Convert accumulated binary coverage to cobertura
        bool merged = CoverageMerger.TryMerge(xmlFile, new[] { accFile }, MergeKind.Cobertura);
        if (!merged)
        {
            int rc = RunProcess("dotnet-coverage", $"merge \"{accFile}\" -f cobertura -o \"{xmlFile}\"");
            merged = rc == 0 && File.Exists(xmlFile);
        }
        if (!merged) return null;

        // Run reportgenerator to produce HTML
        if (Directory.Exists(reportDir)) Directory.Delete(reportDir, true);
        Directory.CreateDirectory(reportDir);
        int rgRc = RunProcess("reportgenerator",
            $"-reports:\"{xmlFile}\" -targetdir:\"{reportDir}\" -reporttypes:Html");
        if (rgRc != 0 || !Directory.GetFiles(reportDir, "*.htm", SearchOption.AllDirectories).Any())
            return null;

        // Zip the HTML report directory
        if (File.Exists(zipFile)) File.Delete(zipFile);
        ZipFile.CreateFromDirectory(reportDir, zipFile);
        return File.ReadAllBytes(zipFile);
    }
    finally
    {
        if (File.Exists(xmlFile)) File.Delete(xmlFile);
        if (File.Exists(zipFile)) File.Delete(zipFile);
        if (Directory.Exists(reportDir)) Directory.Delete(reportDir, true);
    }
}

static void WriteText(HttpListenerResponse res, int status, string body,
    string contentType = "text/plain")
{
    byte[] bytes = Encoding.UTF8.GetBytes(body);
    res.StatusCode = status;
    res.ContentType = contentType + "; charset=utf-8";
    res.ContentLength64 = bytes.Length;
    res.OutputStream.Write(bytes);
    res.OutputStream.Close();
}

/// Starts a long-running background process (does not wait for it to exit).
static Process StartBackground(string fileName, string arguments)
{
    var proc = new Process
    {
        StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
        }
    };
    proc.Start();
    return proc;
}

/// Runs a process and waits up to 60 seconds for it to finish.
static int RunProcess(string fileName, string arguments)
{
    var proc = new Process
    {
        StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }
    };
    proc.Start();
    proc.WaitForExit(60_000);
    return proc.ExitCode;
}

/// Merges coverage files in-process using Microsoft.CodeCoverage.Core's
/// public CoverageFileUtilityV2 API instead of spawning `dotnet-coverage
/// merge` as a subprocess.
///
/// Rationale: the `dotnet-coverage merge` CLI command re-builds a large
/// dependency injection graph, configures file logging, and parses
/// command-line arguments on every invocation before doing the (comparatively
/// tiny) actual merge/conversion work. Calling the underlying conversion
/// routine directly skips all of that fixed overhead. This routine is public
/// API surface (has XML doc comments) shipped inside the officially
/// published `Microsoft.CodeCoverage` NuGet package
/// (`build/netstandard2.0/Microsoft.CodeCoverage.Core.dll`), but it is not
/// exposed via a `lib/` reference and has no formal support/versioning
/// contract for external callers, so every step here is defensive: if the
/// assembly can't be found, doesn't expose the expected members, or throws,
/// callers fall back to spawning `dotnet-coverage merge` as a subprocess
/// (slower, but always correct).
///
/// We locate the DLL inside the already-required `dotnet-coverage` global
/// tool's install directory rather than adding a NuGet package reference:
/// that directory already contains a mutually-compatible, self-contained
/// set of the library and its dependencies (Mono.Cecil, etc.), avoiding any
/// version-mismatch risk between a separately-restored package and the
/// installed CLI tool version.
static class CoverageMerger
{
    private static readonly object InitLock = new();
    private static object? utility;
    private static MethodInfo? mergeMethod;
    private static Type? mergeOperationEnumType;
    private static bool initFailed;

    /// Attempts an in-process merge/conversion. Returns true if the output
    /// file was written successfully; false if in-process invocation is
    /// unavailable or failed (caller should fall back to the CLI subprocess).
    public static bool TryMerge(string outputPath, string[] inputFiles, MergeKind kind)
    {
        if (!EnsureInitialized()) return false;
        try
        {
            string operationName = kind switch
            {
                MergeKind.Cobertura => "MergeToCobertura",
                MergeKind.Coverage => "MergeToCoverage",
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            };
            object mergeOperation = Enum.Parse(mergeOperationEnumType!, operationName);
            var task = (Task<string[]>)mergeMethod!.Invoke(
                utility, new object[] { outputPath, inputFiles, mergeOperation, CancellationToken.None })!;
            task.GetAwaiter().GetResult();
            return File.Exists(outputPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"In-process coverage merge failed ({ex.Message}); falling back to subprocess for this call");
            return false;
        }
    }

    private static bool EnsureInitialized()
    {
        if (utility is not null) return true;
        if (initFailed) return false;
        lock (InitLock)
        {
            if (utility is not null) return true;
            if (initFailed) return false;
            try
            {
                string? dllPath = FindCoreDll();
                if (dllPath is null)
                {
                    Console.Error.WriteLine(
                        "Microsoft.CodeCoverage.Core.dll not found; merge operations will use " +
                        "the dotnet-coverage CLI subprocess instead (slower)");
                    initFailed = true;
                    return false;
                }

                var loadContext = new IsolatedCoverageCoreLoadContext(dllPath);
                Assembly asm = loadContext.LoadFromAssemblyPath(dllPath);
                Type utilType = asm.GetType("Microsoft.CodeCoverage.Core.CoverageFileUtilityV2")
                    ?? throw new InvalidOperationException("CoverageFileUtilityV2 type not found");
                Type cfgType = asm.GetType("Microsoft.CodeCoverage.Core.CoverageFileConfiguration")
                    ?? throw new InvalidOperationException("CoverageFileConfiguration type not found");
                mergeOperationEnumType = asm.GetType("Microsoft.CodeCoverage.Core.CoverageMergeOperation")
                    ?? throw new InvalidOperationException("CoverageMergeOperation type not found");

                object cfg = Activator.CreateInstance(cfgType)!;
                utility = Activator.CreateInstance(utilType, cfg)!;
                mergeMethod = utilType.GetMethod("MergeCoverageFilesAsync")
                    ?? throw new InvalidOperationException("MergeCoverageFilesAsync method not found");

                Console.WriteLine($"Loaded Microsoft.CodeCoverage.Core in-process from {dllPath}");
                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"Failed to load Microsoft.CodeCoverage.Core in-process ({ex.Message}); " +
                    "merge operations will use the dotnet-coverage CLI subprocess instead (slower)");
                initFailed = true;
                return false;
            }
        }
    }

    /// Locates Microsoft.CodeCoverage.Core.dll inside the currently
    /// installed `dotnet-coverage` global tool's store directory:
    /// ~/.dotnet/tools/.store/dotnet-coverage/&lt;version&gt;/dotnet-coverage/&lt;version&gt;/tools/&lt;tfm&gt;/any/Microsoft.CodeCoverage.Core.dll
    private static string? FindCoreDll() => DotnetCoverageToolFiles.Find("Microsoft.CodeCoverage.Core.dll");
}

/// Shared helper for locating DLLs inside the installed `dotnet-coverage`
/// global tool's `.store` directory, used by both <see cref="CoverageMerger"/>
/// and <see cref="SnapshotClient"/> to find their respective in-process
/// reflection targets without adding separate NuGet package references.
static class DotnetCoverageToolFiles
{
    public static string? Find(string fileName)
    {
        string toolsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "tools");
        string storeDir = Path.Combine(toolsDir, ".store", "dotnet-coverage");
        if (!Directory.Exists(storeDir)) return null;

        return Directory.GetFiles(storeDir, fileName, SearchOption.AllDirectories)
            .OrderByDescending(f => f) // prefer the lexicographically-highest (newest) version path
            .FirstOrDefault();
    }
}

enum MergeKind
{
    Cobertura,
    Coverage,
}

/// Isolated load context for Microsoft.CodeCoverage.Core.dll and its
/// dependencies (Mono.Cecil, etc.), resolving them from the tool's own
/// install directory so they can't collide with this agent's own
/// dependencies.
sealed class IsolatedCoverageCoreLoadContext : AssemblyLoadContext
{
    private readonly string baseDir;

    public IsolatedCoverageCoreLoadContext(string mainDllPath) : base("coverage-core-inproc", isCollectible: false)
    {
        baseDir = Path.GetDirectoryName(mainDllPath)!;
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        string candidate = Path.Combine(baseDir, assemblyName.Name + ".dll");
        return File.Exists(candidate) ? LoadFromAssemblyPath(candidate) : null;
    }
}

/// Takes coverage snapshots in-process via the same named-pipe IPC call that
/// the `dotnet-coverage snapshot` CLI command uses internally
/// (`LoggerClient.SendSnapshotMessage`), instead of spawning a subprocess.
///
/// Rationale: measurements in this environment showed `dotnet-coverage
/// snapshot` (as a CLI subprocess) costs ~160-280ms per call almost entirely
/// due to fixed CLI bootstrap cost (DI graph construction, Serilog setup,
/// System.CommandLine parsing) -- even an invocation that does no real work
/// (e.g. a bad-argument error path) takes about the same time. The actual
/// named-pipe round-trip, once a `LoggerClient` is connected, measured at
/// ~1-4ms per call (with a one-time ~60ms pipe-connect cost for the first
/// call on a session). This routine reuses one connected `LoggerClient` per
/// session across calls to amortize that connect cost.
///
/// WARNING - higher risk than <see cref="CoverageMerger"/>: every type used
/// here (`LoggerClient`, `LoggerClientFactory`, `PlatformEnvironment`,
/// `PipeHelper`, ...) is `internal` to Microsoft.CodeCoverage's assemblies,
/// spread across three separate DLLs (`Microsoft.CodeCoverage.Interprocess.dll`,
/// `Microsoft.CodeCoverage.Core.dll`,
/// `Microsoft.CodeCoverage.Instrumentation.Core.dll`). None of this is
/// public/documented/versioned API -- unlike `CoverageFileUtilityV2`, it has
/// no support contract at all and could be renamed or restructured in any
/// `dotnet-coverage` release without notice. Every step is defensive: if any
/// type/member lookup fails, or any call throws, the affected session falls
/// back to spawning `dotnet-coverage snapshot` as a subprocess (slower, but
/// always correct), and this class permanently disables itself (does not
/// keep retrying the broken reflection path on every future call).
static class SnapshotClient
{
    private static readonly object InitLock = new();
    private static bool initFailed;
    private static object? environment;
    private static object? loggerClientFactory;
    private static MethodInfo? createLoggerClientMethod;
    private static MethodInfo? sendSnapshotMethod;

    private static readonly ConcurrentDictionary<string, object> loggerClients = new();

    /// Attempts an in-process snapshot. Returns true if the output file was
    /// written successfully; false if in-process invocation is unavailable
    /// or failed (caller should fall back to the CLI subprocess for this call).
    public static bool TrySnapshot(string sessionId, string outputPath, bool reset, string tagId = "", string tagName = "")
    {
        if (!EnsureInitialized()) return false;

        object? client = null;
        try
        {
            client = loggerClients.GetOrAdd(sessionId, id =>
                createLoggerClientMethod!.Invoke(loggerClientFactory, new object[] { id, NullLoggerInstance!, 30_000 })!);

            sendSnapshotMethod!.Invoke(client, new object[] { outputPath, reset, tagId, tagName });
            return File.Exists(outputPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"In-process coverage snapshot failed ({ex.Message}); falling back to subprocess for this call");
            // The cached client may be wrapping a broken/disconnected pipe (e.g. the
            // `dotnet-coverage collect --server-mode` process restarted); evict it so
            // the next call builds a fresh one instead of repeatedly failing.
            if (client is not null && loggerClients.TryRemove(sessionId, out _))
                (client as IDisposable)?.Dispose();
            return false;
        }
    }

    private static object? NullLoggerInstance;

    private static bool EnsureInitialized()
    {
        if (sendSnapshotMethod is not null) return true;
        if (initFailed) return false;
        lock (InitLock)
        {
            if (sendSnapshotMethod is not null) return true;
            if (initFailed) return false;
            try
            {
                string? corePath = DotnetCoverageToolFiles.Find("Microsoft.CodeCoverage.Core.dll");
                string? ipcPath = DotnetCoverageToolFiles.Find("Microsoft.CodeCoverage.Interprocess.dll");
                string? instPath = DotnetCoverageToolFiles.Find("Microsoft.CodeCoverage.Instrumentation.Core.dll");
                if (corePath is null || ipcPath is null || instPath is null)
                {
                    Console.Error.WriteLine(
                        "Microsoft.CodeCoverage.{Core,Interprocess,Instrumentation.Core}.dll not all found; " +
                        "snapshot operations will use the dotnet-coverage CLI subprocess instead (slower)");
                    initFailed = true;
                    return false;
                }

                var loadContext = new IsolatedCoverageCoreLoadContext(corePath);
                Assembly core = loadContext.LoadFromAssemblyPath(corePath);
                Assembly ipc = loadContext.LoadFromAssemblyPath(ipcPath);
                Assembly inst = loadContext.LoadFromAssemblyPath(instPath);

                Type fileHelperType = core.GetType("Microsoft.CodeCoverage.Core.Utils.FileHelper")
                    ?? throw new InvalidOperationException("FileHelper type not found");
                Type runtimeInfoHelperType = core.GetType("Microsoft.CodeCoverage.Core.Utils.RuntimeInformationHelper")
                    ?? throw new InvalidOperationException("RuntimeInformationHelper type not found");
                Type envType = core.GetType("Microsoft.CodeCoverage.Core.PlatformEnvironment")
                    ?? throw new InvalidOperationException("PlatformEnvironment type not found");
                Type envIfaceType = core.GetType("Microsoft.CodeCoverage.Core.IEnvironment")
                    ?? throw new InvalidOperationException("IEnvironment type not found");
                Type nullLoggerType = core.GetType("Microsoft.CodeCoverage.Core.Logger.NullLogger")
                    ?? throw new InvalidOperationException("NullLogger type not found");
                Type pipeHelperType = inst.GetType("Microsoft.CodeCoverage.Instrumentation.Core.Tracker.PipeHelper")
                    ?? throw new InvalidOperationException("PipeHelper type not found");
                Type lcfType = ipc.GetType("Microsoft.CodeCoverage.Interprocess.LoggerClientFactory")
                    ?? throw new InvalidOperationException("LoggerClientFactory type not found");
                Type lcfIfaceType = ipc.GetType("Microsoft.CodeCoverage.Interprocess.ILoggerClientFactory")
                    ?? throw new InvalidOperationException("ILoggerClientFactory type not found");
                Type loggerClientIfaceType = ipc.GetType("Microsoft.CodeCoverage.Interprocess.ILoggerClient")
                    ?? throw new InvalidOperationException("ILoggerClient type not found");

                object fileHelper = NewNonPublic(fileHelperType);
                object runtimeInfoHelper = NewNonPublic(runtimeInfoHelperType);
                environment = NewNonPublic(envType, fileHelper, runtimeInfoHelper);
                NullLoggerInstance = NewNonPublic(nullLoggerType);
                loggerClientFactory = NewNonPublic(lcfType, environment);

                createLoggerClientMethod = lcfIfaceType.GetMethod("CreateLoggerClient")
                    ?? throw new InvalidOperationException("CreateLoggerClient method not found");
                sendSnapshotMethod = loggerClientIfaceType.GetMethod("SendSnapshotMessage")
                    ?? throw new InvalidOperationException("SendSnapshotMessage method not found");

                // Referenced only to confirm PipeHelper resolves; not otherwise needed
                // here since LoggerClientFactory computes the pipe path itself.
                _ = pipeHelperType;

                Console.WriteLine($"Loaded Microsoft.CodeCoverage snapshot IPC path in-process from {corePath}");
                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"Failed to load in-process snapshot IPC path ({ex.Message}); " +
                    "snapshot operations will use the dotnet-coverage CLI subprocess instead (slower)");
                initFailed = true;
                return false;
            }
        }
    }

    private static object NewNonPublic(Type type, params object[] args)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        ConstructorInfo ctor = type.GetConstructors(flags).First(c => c.GetParameters().Length == args.Length)
            ?? throw new InvalidOperationException($"No matching constructor found on {type.FullName}");
        return ctor.Invoke(args);
    }

    /// Disposes all cached per-session LoggerClient connections. Call during
    /// agent shutdown.
    public static void DisposeAll()
    {
        foreach (var kvp in loggerClients)
        {
            (kvp.Value as IDisposable)?.Dispose();
        }
        loggerClients.Clear();
    }
}
