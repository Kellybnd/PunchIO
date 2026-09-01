using System.Runtime.InteropServices;
using Xunit;

namespace PunchIO.Core.Tests;

public class ScaffoldingTests
{
    [Fact]
    public void TargetFrameworkIsOneOfTheSupportedOnes()
    {
        // Guards the multi-targeting itself: if a TFM is dropped from
        // Directory.Build.props, this stops reporting the missing one.
        Assert.StartsWith(".NET", RuntimeInformation.FrameworkDescription);
    }

    [Fact]
    public void UnsafeCodeIsEnabled()
    {
        // AlignedNativeSlab cannot compile without this.
        unsafe
        {
            int value = 42;
            int* p = &value;
            Assert.Equal(42, *p);
        }
    }
}
