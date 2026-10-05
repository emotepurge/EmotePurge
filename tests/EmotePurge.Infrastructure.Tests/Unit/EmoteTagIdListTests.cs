using EmotePurge.Core.Entities;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

// The id-list rule the tag service and the Api's report handlers share (#201 T-C, F2).
public class EmoteTagIdListTests
{
    [Fact]
    public void Check_NullOrNoIds_IsEmpty()
    {
        Assert.Equal(EmoteTagIdListStatus.Empty, EmoteTagIdList.Check(null));
        Assert.Equal(EmoteTagIdListStatus.Empty, EmoteTagIdList.Check([]));
    }

    [Fact]
    public void Check_UpToTheRawLimit_IsOk_CountingDuplicates_OneMoreIsInvalid()
    {
        var atLimit = Enumerable.Repeat("01J94NYQR0000D15QN0BDGN85E", EmoteTagLimits.MaxIdsPerRequest).ToList();

        Assert.Equal(EmoteTagIdListStatus.Ok, EmoteTagIdList.Check(atLimit));
        Assert.Equal(EmoteTagIdListStatus.Invalid, EmoteTagIdList.Check([.. atLimit, "01J94NYQR0000D15QN0BDGN85E"]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("../etc")]
    [InlineData("01J94NYQR0000D15QN0BDGN85E\n")]
    [InlineData("012345678901234567890123456789012")]
    public void Check_AnyUnfitId_IsInvalid(string unfit) =>
        Assert.Equal(EmoteTagIdListStatus.Invalid, EmoteTagIdList.Check(["01J94NYQR0000D15QN0BDGN85E", unfit]));
}
