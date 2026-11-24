namespace DWIS.Blackboard.Conformance.Tests;

/// <summary>
/// T-MOD-7: the suite run in-process against a broken endpoint fails the check that matches the flaw. The suite's own
/// manifest is the only one injected; what the other checks say does not matter here.
/// </summary>
public sealed class BrokenEndpointTests
{
    private static (IReadOnlyList<Check> Checks, string Lines) RunAgainst(BrokenBlackboard endpoint)
    {
        using var blackboard = Blackboard.Connect(endpoint.EndpointUrl);
        var lines = new List<string>();
        var suite = new Suite(blackboard, [], c => lines.Add($"{(c.Passed ? "PASS" : "FAIL")} {c.Id} {c.Name}{(c.Passed ? "" : ": " + c.Reason)}"));
        suite.Run();
        return (suite.Checks, string.Join(" | ", lines));
    }

    [Fact]
    public async Task An_endpoint_whose_object_is_not_under_Objects_fails_C01_and_nothing_else_runs()
    {
        await using var endpoint = await BrokenBlackboard.StartAsync(Flaw.ObjectElsewhere);

        var (checks, lines) = RunAgainst(endpoint);

        var first = checks.Should().ContainSingle(lines).Subject;
        first.Id.Should().Be("C-01");
        first.Passed.Should().BeFalse();
        first.Reason.Should().Contain("no object named DDHubServer under Objects");
    }

    [Fact]
    public async Task An_endpoint_without_validity_booleans_passes_C01_to_C03_and_fails_C04()
    {
        await using var endpoint = await BrokenBlackboard.StartAsync(Flaw.NoValidityBooleans);

        var (checks, lines) = RunAgainst(endpoint);

        checks.Where(c => c.Id is "C-01" or "C-02" or "C-03").Should().OnlyContain(c => c.Passed, "the object, its methods and the inject are as declared: {0}", lines);
        // Without a manifest folder the suite injects its own manifest, after registering a query on it; C-04 then runs on it.
        var validity = checks.Where(c => c.Id == "C-04").ToList();
        validity.Should().NotBeEmpty("the suite checks the validity booleans of what it injected: {0}", lines);
        validity.Should().OnlyContain(c => !c.Passed && c.Reason.Contains("validity boolean"), lines);
    }
}
