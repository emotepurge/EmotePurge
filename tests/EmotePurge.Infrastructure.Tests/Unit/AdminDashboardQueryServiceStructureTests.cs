using System.Text.RegularExpressions;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

// The admin dashboard deliberately does not read UsageStats (operator decision 2026-10-11, spec
// section 11 A): the slot epic reshapes that table, and the dashboard's activity series comes from
// ChannelLiveDays, its workload from the system-load rollups. This turns the decision into a red
// build rather than a review comment.
//
// Form: a source scan of the files that make up the dashboard query, not IL inspection. The IL form
// (MethodBody.GetILAsByteArray plus token resolution) would also have to walk the compiler-generated
// async state machines and lambda closures nested in the type and still miss a raw SQL string naming
// the table; the text scan catches the DbSet, the entity type and a quoted "UsageStats" in SQL alike,
// for every AdminDashboard*.cs file in Infrastructure/Services, at the cost of also flagging the word
// in a comment (acceptable: the files have no reason to mention it).
public class AdminDashboardQueryServiceStructureTests
{
    [Fact]
    public void AdminDashboardQuery_DoesNotReferenceUsageStats()
    {
        // Every file of the dashboard read side, so a new split-out file is covered without a list.
        var files = Directory.GetFiles(FindServicesDirectory(), "AdminDashboard*.cs");
        Assert.Contains(files, file => file.EndsWith("AdminDashboardQueryService.cs", StringComparison.Ordinal));
        Assert.Contains(files, file => file.EndsWith("AdminDashboardRawLoadQuery.cs", StringComparison.Ordinal));

        foreach (var file in files)
        {
            var matches = Regex.Matches(File.ReadAllText(file), @"\bUsageStats?\b");
            Assert.True(
                matches.Count == 0,
                $"{Path.GetFileName(file)} must not read UsageStats (operator decision 2026-10-11, #381).");
        }
    }

    private static string FindServicesDirectory()
    {
        const string relative = "src/EmotePurge.Infrastructure/Services";
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException($"Could not locate {relative} above {AppContext.BaseDirectory}.");
    }
}
