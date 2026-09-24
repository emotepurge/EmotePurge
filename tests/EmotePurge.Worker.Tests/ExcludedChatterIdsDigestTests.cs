using EmotePurge.Worker.Harness;
using Xunit;

namespace EmotePurge.Worker.Tests;

// GDPR Art. 21 objection gate (issue #252/#260, P1 Codex finding): a changed exclusion policy has
// to move HarnessRunIdentity so a resumed or recomputed run cannot silently keep day lines counted
// under a different policy — but the ids themselves must never end up in a file that outlives the
// run. This digest is that fingerprint, and these tests pin both halves of that contract.
public class ExcludedChatterIdsDigestTests
{
    [Fact]
    public void SameIdsInAnotherOrder_HashesTheSame()
    {
        var forward = ExcludedChatterIdsDigest.Compute(["111", "222", "333"]);
        var backward = ExcludedChatterIdsDigest.Compute(["333", "111", "222"]);

        Assert.Equal(forward, backward);
    }

    [Fact]
    public void SameIdsWithDifferentSurroundingWhitespace_HashesTheSame()
    {
        var trimmed = ExcludedChatterIdsDigest.Compute(["111", "222"]);
        var padded = ExcludedChatterIdsDigest.Compute(["  111", "222   "]);

        Assert.Equal(trimmed, padded);
    }

    [Fact]
    public void ADifferentIdSet_ChangesTheHash()
    {
        var before = ExcludedChatterIdsDigest.Compute(["111", "222"]);
        var after = ExcludedChatterIdsDigest.Compute(["111", "222", "333"]);

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void ARemovedId_ChangesTheHash()
    {
        var before = ExcludedChatterIdsDigest.Compute(["111", "222"]);
        var after = ExcludedChatterIdsDigest.Compute(["111"]);

        Assert.NotEqual(before, after);
    }

    // The empty case has to be a fixed value, not merely "some hash": every no-exclusion run under
    // a given AlgorithmVersion must land on the same identity for this field, exactly as it would
    // if the field never existed.
    [Fact]
    public void AnEmptyList_HashesToAFixedStableValue()
    {
        var first = ExcludedChatterIdsDigest.Compute([]);
        var second = ExcludedChatterIdsDigest.Compute(Array.Empty<string>());

        Assert.Equal(first, second);
        Assert.Equal(64, first.Length);
    }

    [Fact]
    public void BlankAndWhitespaceOnlyEntries_AreIgnored_LikeTheFilterItselfIgnoresThem()
    {
        var withoutBlanks = ExcludedChatterIdsDigest.Compute(["111"]);
        var withBlanks = ExcludedChatterIdsDigest.Compute(["111", "", "   "]);

        Assert.Equal(withoutBlanks, withBlanks);
    }

    [Fact]
    public void ADuplicatedId_DoesNotChangeTheHash()
    {
        var single = ExcludedChatterIdsDigest.Compute(["111"]);
        var duplicated = ExcludedChatterIdsDigest.Compute(["111", "111"]);

        Assert.Equal(single, duplicated);
    }

    [Fact]
    public void TheHash_IsALowercaseSha256Hex()
    {
        var hash = ExcludedChatterIdsDigest.Compute(["111", "222"]);

        Assert.Equal(64, hash.Length);
        Assert.All(hash, c => Assert.True(char.IsAsciiDigit(c) || (c is >= 'a' and <= 'f')));
    }

    // The whole point of hashing rather than carrying the list, spelled out as a test: no raw id
    // may appear anywhere in the digest's own output.
    [Fact]
    public void TheHash_NeverContainsARawId()
    {
        var hash = ExcludedChatterIdsDigest.Compute(["918273645", "554433221"]);

        Assert.DoesNotContain("918273645", hash, StringComparison.Ordinal);
        Assert.DoesNotContain("554433221", hash, StringComparison.Ordinal);
    }
}
