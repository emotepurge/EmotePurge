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
// for every file listed here, at the cost of also flagging the word in a comment (acceptable: the
// files have no reason to mention it).
public class AdminDashboardQueryServiceStructureTests
{
    [Theory]
    [InlineData("src/EmotePurge.Infrastructure/Services/AdminDashboardQueryService.cs")]
    [InlineData("src/EmotePurge.Infrastructure/Services/AdminDashboardLoadBucketing.cs")]
    public void AdminDashboardQuery_DoesNotReferenceUsageStats(string relativePath)
    {
        var source = File.ReadAllText(FindSourceFile(relativePath));

        var matches = Regex.Matches(source, @"\bUsageStats?\b")
            .Select(m => m.Value)
            .ToList();

        Assert.True(
            matches.Count == 0,
            $"{relativePath} must not read UsageStats (operator decision 2026-10-11, #381).");
    }

    private static string FindSourceFile(string relative)
    {
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
