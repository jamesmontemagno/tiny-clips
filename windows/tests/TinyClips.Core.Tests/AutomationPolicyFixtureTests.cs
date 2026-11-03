using System.Text.Json;
using TinyClips.Core.Automation;

namespace TinyClips.Core.Tests;

public sealed class AutomationPolicyFixtureTests
{
    private static string FixturePath => Path.Combine(AppContext.BaseDirectory, "AutomationFixtures", "policy.json");

    [Fact]
    public void Decisions_MatchSharedPolicyFixture()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FixturePath));
        var policy = new AutomationPolicy(AutomationContract.Default);
        var cases = document.RootElement.GetProperty("cases").EnumerateArray().ToArray();
        Assert.NotEmpty(cases);

        var failures = new List<string>();
        foreach (var testCase in cases)
        {
            var name = testCase.GetProperty("name").GetString();
            var enabled = testCase.GetProperty("enabled").GetBoolean();
            var scopesElement = testCase.GetProperty("scopes");
            IReadOnlyCollection<string>? scopes = scopesElement.ValueKind == JsonValueKind.Null
                ? null
                : scopesElement.EnumerateArray().Select(e => e.GetString()!).ToArray();
            var command = testCase.GetProperty("command").GetString()!;
            var expected = testCase.GetProperty("decision").GetString();

            var actual = policy.Decide(enabled, scopes, command).ToString();
            if (actual != expected)
            {
                failures.Add($"{name}: expected {expected}, got {actual}");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void Decisions_UseOnlyContractErrorCodes()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FixturePath));
        var contract = AutomationContract.Default;
        foreach (var testCase in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            var decision = testCase.GetProperty("decision").GetString()!;
            Assert.True(
                decision is "allow" or "approve" || contract.Errors.ContainsKey(decision),
                $"Unknown decision '{decision}'.");
        }
    }
}
