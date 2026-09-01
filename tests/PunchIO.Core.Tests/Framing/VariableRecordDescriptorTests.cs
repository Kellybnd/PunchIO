using PunchIO.Framing;
using Xunit;

namespace PunchIO.Core.Tests.Framing;

public class VariableRecordDescriptorTests
{
    [Fact]
    public void MicroFocusPresetMatchesTheSpecifiedLayout()
    {
        var d = VariableRecordDescriptor.MicroFocus;

        Assert.Equal(4, d.PrefixBytes);          // four-byte record header
        Assert.Equal(0, d.SuffixBytes);          // no suffix
        Assert.Equal(0, d.LengthFieldOffset);    // length in bytes 0-1
        Assert.Equal(2, d.LengthFieldWidth);
        Assert.Equal(2, d.FlagByteOffset);       // byte 2 carries flags
        Assert.Equal(Endianness.BigEndian, d.Endianness);
        Assert.Equal(LengthBasis.DataOnly, d.LengthIncludes);
        Assert.Equal(1, d.Alignment);
    }

    [Fact]
    public void FujitsuPresetHasAPrefixAndAMatchingSuffix()
    {
        var d = VariableRecordDescriptor.Fujitsu;

        Assert.Equal(4, d.PrefixBytes);
        Assert.Equal(4, d.SuffixBytes);          // prefix AND postfix
        Assert.Equal(0, d.LengthFieldOffset);
        Assert.Equal(4, d.LengthFieldWidth);
        Assert.Equal(-1, d.FlagByteOffset);      // no flag byte
        Assert.True(d.ValidateSuffix);           // free integrity check
    }

    [Fact]
    public void FujitsuLengthCountsOnlyTheCallerVisibleData()
    {
        // Confirmed by the customer: a record with n data bytes occupies n + 8
        // bytes on disk and reports n. The one Fujitsu fact that is NOT a
        // verification item.
        Assert.Equal(LengthBasis.DataOnly, VariableRecordDescriptor.Fujitsu.LengthIncludes);
    }

    [Fact]
    public void PresetsValidateCleanly()
    {
        VariableRecordDescriptor.MicroFocus.Validate();
        VariableRecordDescriptor.Fujitsu.Validate();
    }

    [Theory]
    [InlineData(0, 2, 0, 1)]    // prefix too small to hold the length field
    [InlineData(4, 0, 0, 1)]    // zero-width length field
    [InlineData(4, 5, 0, 1)]    // length field wider than four bytes
    [InlineData(4, 4, 1, 1)]    // length field runs past the end of the prefix
    [InlineData(4, 2, 0, 0)]    // alignment must be at least one
    [InlineData(4, 2, 0, 3)]    // alignment must be a power of two
    public void RejectsInternallyInconsistentDescriptors(
        int prefixBytes, int lengthFieldWidth, int lengthFieldOffset, int alignment)
    {
        var d = new VariableRecordDescriptor
        {
            PrefixBytes = prefixBytes,
            LengthFieldWidth = lengthFieldWidth,
            LengthFieldOffset = lengthFieldOffset,
            Alignment = alignment,
        };

        Assert.Throws<ArgumentException>(d.Validate);
    }

    [Fact]
    public void RejectsSuffixValidationWhenThereIsNoSuffix()
    {
        var d = VariableRecordDescriptor.MicroFocus with { ValidateSuffix = true };

        Assert.Throws<ArgumentException>(d.Validate);
    }

    [Fact]
    public void SupportsWithExpressionsForOneLineCustomisation()
    {
        // The path a customer takes when a preset is close but not exact.
        var d = VariableRecordDescriptor.Fujitsu with { Endianness = Endianness.LittleEndian };

        Assert.Equal(Endianness.LittleEndian, d.Endianness);
        Assert.Equal(4, d.SuffixBytes);   // everything else preserved
        d.Validate();
    }
}
