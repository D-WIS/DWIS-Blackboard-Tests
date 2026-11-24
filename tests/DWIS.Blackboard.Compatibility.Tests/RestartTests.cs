using System.Diagnostics;
using DWIS.Client.ReferenceImplementation;

namespace DWIS.Blackboard.Compatibility.Tests;

/// <summary>
/// T-RT-6: the real-time process restarts and its address space comes back from the semantic service (R-RT-6).
/// In the stack collection, so that it runs apart from the other tests; the restart closes the fixture's sessions,
/// which it resets at the end. Needs <c>DWIS_COMPOSE_FILE</c> and docker on the path, as in the workflow.
/// </summary>
[Collection("stack")]
public sealed class RestartTests(StackFixture stack)
{
    [Fact]
    public void T_RT_6_After_a_restart_of_the_real_time_process_the_folders_and_variables_come_back()
    {
        Assert.SkipWhen(StackFixture.NoStack, StackFixture.NoStackReason);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(StackFixture.ComposeFile), "DWIS_COMPOSE_FILE is not set: nothing to restart.");
        const string provider = "CompatRestart";
        var manifest = StackFixture.Manifest(provider);
        var before = StackFixture.Connect();
        var injected = before.Inject(manifest);
        injected.Success.Should().BeTrue();
        before.UpdateProvidedVariables([("SPP", 7.0, DateTime.UtcNow)]).Should().BeTrue();

        Restart();
        stack.Reset();

        IOPCUADWISClient? after = null;
        StackFixture.Eventually(() =>
        {
            try { after = StackFixture.Connect(); return true; } catch (Exception) { return false; }
        }, TimeSpan.FromSeconds(120)).Should().BeTrue("the process should answer again after the restart");
        try
        {
            // The structure is rebuilt from the service's list of manifests; the values start again from the
            // manifest's initial value (nothing but the structure is kept, R-RT-6).
            var ns = $"http://ddhub.no/{provider}/Variables/";
            StackFixture.Eventually(() => after!.ReadAnyVariables(out _, (ns, $"{provider}.SPP")), TimeSpan.FromSeconds(60))
                .Should().BeTrue("the provider's variables should come back");
            after!.ReadAnyVariables(out var values, (ns, $"{provider}.SPP"), (ns, $"{provider}.SPP_validitySignal"), (ns, $"{provider}.WOB")).Should().BeTrue();
            values.Should().Equal(12.5, false, 3.0);
            after.GetQueryResult(StackFixture.SignalQuery(provider)).Results.Should().ContainSingle();
        }
        finally
        {
            stack.Delete(manifest).Should().BeTrue();
        }
    }

    private static void Restart()
    {
        using var docker = Process.Start(new ProcessStartInfo("docker", $"compose -f \"{StackFixture.ComposePath()}\" restart realtime")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException("docker did not start");
        docker.WaitForExit(TimeSpan.FromSeconds(120)).Should().BeTrue("docker compose restart should finish");
        docker.ExitCode.Should().Be(0, docker.StandardError.ReadToEnd());
    }
}
