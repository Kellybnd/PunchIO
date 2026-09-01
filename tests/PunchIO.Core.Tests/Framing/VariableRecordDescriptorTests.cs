using PunchIO.Framing;
using Xunit;

namespace PunchIO.Core.Tests.Framing;

public class VariableRecordDescriptorTests
{
    [Fact]
    public void MicroFocusPresetMatchesTheDocumentedLayout()
    {
        var d = VariableRecordDescriptor.MicroFocus();

        Assert.Equal(2, d.PrefixBytes);          // two bytes below 4096
        Assert.Equal(0, d.SuffixBytes);          // no suffix
        Assert.Equal(0, d.LengthFieldOffset);
        Assert.Equal(2, d.LengthFieldWidth);
        Assert.Equal(4, d.StatusBits);           // top four bits are the status
        Assert.Equal(4, d.DataRecordStatus);     // 0100 = user data record
        Assert.Equal(12, d.LengthBits);          // the rest is the length
        Assert.Equal(-1, d.FlagByteOffset);      // no separate flag byte
        Assert.Equal(Endianness.BigEndian, d.Endianness);
        Assert.Equal(LengthBasis.DataOnly, d.LengthIncludes);
        Assert.Equal(4, d.Alignment);            // records start on 4-byte boundaries
        Assert.Equal(VariableFileHeader.MicroFocusStandard, d.FileHeader);
        Assert.Equal(128, d.FileHeaderLength);
        Assert.Equal(4095, d.MaxDataLength);
    }

    [Theory]
    [InlineData(80, 2)]
    [InlineData(4095, 2)]     // the largest a twelve-bit length can describe
    [InlineData(4096, 4)]     // one byte more and the field has to widen
    [InlineData(65535, 4)]
    public void MicroFocusWidensTheControlFieldForLongRecords(int maxRecordLength, int expected)
    {
        var d = VariableRecordDescriptor.MicroFocus(maxRecordLength);

        Assert.Equal(expected, d.PrefixBytes);
        Assert.Equal(expected, d.LengthFieldWidth);
        Assert.Equal(maxRecordLength, d.MaxDataLength);
    }

    [Fact]
    public void MicroFocusRejectsAMaximumItsHeaderCannotRecord()
    {
        // The header keeps the maximum record length in two bytes.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => VariableRecordDescriptor.MicroFocus(65_536));
    }

    [Fact]
    public void RejectsADeclaredMaximumTheLengthFieldCannotDescribe()
    {
        // A file whose header promises 9,000-byte records but whose control
        // field has only twelve bits could never frame them.
        var d = VariableRecordDescriptor.MicroFocus() with { MaxRecordLength = 9_000 };

        Assert.Throws<ArgumentException>(d.Validate);
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

        // Little-endian: the runtime reads and writes the length as a native
        // x86 word with no byte swapping. Confirmed against a working
        // implementation, so this is a fact about the format rather than a
        // default, and reversing it would misread every real file.
        Assert.Equal(Endianness.LittleEndian, d.Endianness);
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
        VariableRecordDescriptor.MicroFocus().Validate();
        VariableRecordDescriptor.MicroFocus(65_535).Validate();
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
        var d = VariableRecordDescriptor.MicroFocus() with { ValidateSuffix = true };

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
