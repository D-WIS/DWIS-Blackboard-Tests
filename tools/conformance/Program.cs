// The conformance suite, first version (M4.5, Tests #23; R-MOD-3, Appendix H.5): runnable checks of the behaviour
// the clients depend on, against any Blackboard endpoint.
//
//   dotnet run --project tools/conformance -- --endpoint opc.tcp://host:48030 [--manifests DIR] [--manifest FILE]...
//                                             [--report-json FILE] [--report FILE]
//
// --manifests names a folder with one folder per manifest (manifest.json and, when there is one, expected-report.json
// whose "status" 400 says the service refuses it), as fixtures/manifests/library; --manifest adds one file. The
// suite's own small manifest is always used, for the diff check. Each check prints one line, PASS or FAIL with the
// reason; the JSON report lists them all. Exit code 0 when every check passed, 1 otherwise, 2 for a usage error.
using System.Globalization;
using System.Text.Json;
using DWIS.Blackboard.Conformance;

string? endpoint = null;
string? manifestsFolder = null;
var manifestFiles = new List<string>();
string? reportJson = null;
string? reportText = null;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--endpoint": endpoint = args[++i]; break;
        case "--manifests": manifestsFolder = args[++i]; break;
        case "--manifest": manifestFiles.Add(args[++i]); break;
        case "--report-json": reportJson = args[++i]; break;
        case "--report": reportText = args[++i]; break;
        case "--help" or "-h":
            Console.WriteLine("dotnet run --project tools/conformance -- --endpoint opc.tcp://host:48030 [--manifests DIR] [--manifest FILE]... [--report-json FILE] [--report FILE]");
            return 0;
        default:
            Console.Error.WriteLine($"unknown option {args[i]}");
            return 2;
    }
}
if (endpoint is null)
{
    Console.Error.WriteLine("--endpoint is required");
    return 2;
}

var manifests = new List<ManifestCase>();
if (manifestsFolder is not null)
{
    foreach (var folder in Directory.GetDirectories(manifestsFolder).OrderBy(f => f, StringComparer.Ordinal))
    {
        var file = Path.Combine(folder, "manifest.json");
        if (!File.Exists(file))
        {
            continue;
        }
        var expected = Path.Combine(folder, "expected-report.json");
        var refused = File.Exists(expected) && JsonDocument.Parse(File.ReadAllText(expected)).RootElement.TryGetProperty("status", out var status) && status.GetInt32() >= 400;
        manifests.Add(new ManifestCase(Path.GetFileName(folder), File.ReadAllText(file), refused));
    }
}
foreach (var file in manifestFiles)
{
    manifests.Add(new ManifestCase(Path.GetFileName(file), File.ReadAllText(file), false));
}

var lines = new List<string>();
void Print(Check check)
{
    var line = check.Passed ? $"PASS  {check.Id} {check.Name} ({check.Ms} ms)" : $"FAIL  {check.Id} {check.Name}: {check.Reason} ({check.Ms} ms)";
    Console.WriteLine(line);
    lines.Add(line);
}

var ranAt = DateTime.UtcNow;
Console.WriteLine($"conformance suite against {endpoint}, {manifests.Count} manifest(s) plus the suite's own, {ranAt:yyyy-MM-dd HH:mm:ss} UTC");
bool passed;
IReadOnlyList<Check> checks;
try
{
    using var blackboard = Blackboard.Connect(endpoint);
    var suite = new Suite(blackboard, manifests, Print);
    passed = suite.Run();
    checks = suite.Checks;
}
catch (Exception e)
{
    var failed = new Check("C-00", "the endpoint answers", false, e.GetType().Name + ": " + e.Message, 0);
    Print(failed);
    passed = false;
    checks = [failed];
}
var summary = $"{checks.Count(c => c.Passed)} passed, {checks.Count(c => !c.Passed)} failed";
Console.WriteLine(passed ? $"conformance: every check passed ({summary})" : $"conformance: FAILED ({summary})");
lines.Add(summary);

if (reportJson is not null)
{
    var report = new { endpoint, ranAt = ranAt.ToString("o", CultureInfo.InvariantCulture), passed = checks.Count(c => c.Passed), failed = checks.Count(c => !c.Passed), checks };
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportJson))!);
    File.WriteAllText(reportJson, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
}
if (reportText is not null)
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportText))!);
    File.WriteAllLines(reportText, lines);
}
return passed ? 0 : 1;
