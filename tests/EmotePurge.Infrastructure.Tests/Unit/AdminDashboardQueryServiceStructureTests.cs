using System.Text.RegularExpressions;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

// The admin dashboard deliberately does not read UsageStats (operator decision 2026-10-11, spec
// section 11 A): the slot epic reshapes that table, and the dashboard's activity series comes from
// ChannelLiveDays. This turns the decision into a red build rather than a review comment.
public class AdminDashboardQueryServiceStructureTests
{
    [Fact]
    public void AdminDashboardQueryService_DoesNotReferenceUsageStats()
    {
        var source = File.ReadAllText(FindSourceFile());

        var matches = Regex.Matches(source, @"\bUsageStats?\b")
            .Select(m => m.Value)
            .ToList();

        Assert.True(
            matches.Count == 0,
            "AdminDashboardQueryService must not read UsageStats (operator decision 2026-10-11, #381).");
    }

    private static string FindSourceFile()
    {
        const string relative = "src/EmotePurge.Infrastructure/Services/AdminDashboardQueryService.cs";
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"Could not locate {relative} above {AppContext.BaseDirectory}.");
    }
}
