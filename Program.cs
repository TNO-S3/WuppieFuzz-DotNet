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
//   Cobertura XML with `dotnet-coverage merge --output-format cobertura`, and
//   returns the XML.  Snapshots are also accumulated for the final /report.
//
// Performance note:
//   /coverage is called on every fuzzing iteration, so it is on the fuzzer's
//   hot path. Folding each snapshot into `accumulated.coverage` requires an
//   additional `dotnet-coverage merge` subprocess spawn, which is expensive
//   (full CLR/JIT cold start). To keep that cost off the hot path, snapshots
//   are handed to a single background worker thread that merges them
//   sequentially; the HTTP response for /coverage is sent as soon as the
//   (already unavoidable) snapshot + cobertura-conversion steps are done.
//   /report drains this queue before building the final report so it never
//   observes a stale accumulation.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
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
        string resetFlag = reset ? "--reset" : "";
        int rc = RunProcess("dotnet-coverage", $"snapshot {sessionId} {resetFlag} -o \"{snapFile}\"");
        if (rc != 0 || !File.Exists(snapFile))
        {
            Console.Error.WriteLine("dotnet-coverage snapshot returned no output");
            return "<coverage version=\"1\"><packages/></coverage>";
        }

        // Convert binary .coverage -> cobertura XML for the Rust client
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
            int rc = RunProcess("dotnet-coverage",
                $"merge \"{accFile}\" \"{snapFile}\" -f coverage -o \"{tmpAcc}\"");
            if (rc == 0 && File.Exists(tmpAcc))
                File.Move(tmpAcc, accFile, overwrite: true);
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
            RunProcess("dotnet-coverage", $"snapshot {sessionId} -o \"{snapFile}\"");
            if (File.Exists(snapFile))
                File.Move(snapFile, accFile, overwrite: true);
            else
                return null;
        }

        // Convert accumulated binary coverage to cobertura
        int rc = RunProcess("dotnet-coverage", $"merge \"{accFile}\" -f cobertura -o \"{xmlFile}\"");
        if (rc != 0 || !File.Exists(xmlFile)) return null;

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
