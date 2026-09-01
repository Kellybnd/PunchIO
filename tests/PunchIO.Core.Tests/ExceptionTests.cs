using Xunit;

namespace PunchIO.Core.Tests;

public class ExceptionTests
{
    [Theory]
    [InlineData("00")] // success
    [InlineData("10")] // end of file
    [InlineData("23")] // record not found
    [InlineData("35")] // file not found on open
    [InlineData("39")] // attribute mismatch
    public void KnownStatusCodesAreExactlyTwoCharacters(string code)
    {
        var status = new FileStatus(code);

        Assert.Equal(code, status.Code);
        Assert.Equal(2, status.Code.Length);
    }

    [Fact]
    public void StatusCodeMustBeTwoCharacters()
    {
        Assert.Throws<ArgumentException>(() => new FileStatus("9"));
        Assert.Throws<ArgumentException>(() => new FileStatus("000"));
    }

    [Fact]
    public void WellKnownStatusesHaveTheirSpecCodes()
    {
        Assert.Equal("00", FileStatus.Ok.Code);
        Assert.Equal("10", FileStatus.EndOfFile.Code);
        Assert.Equal("23", FileStatus.RecordNotFound.Code);
        Assert.Equal("35", FileStatus.FileNotFound.Code);
        Assert.Equal("39", FileStatus.AttributeMismatch.Code);
        Assert.Equal("90", FileStatus.PermanentError.Code);
    }

    [Fact]
    public void RecordFormatExceptionCarriesTheByteOffset()
    {
        var ex = new RecordFormatException("prefix length exceeds remaining data", 200_000_000_017);

        Assert.Equal(200_000_000_017, ex.ByteOffset);
        Assert.Equal(FileStatus.PermanentError, ex.Status);
        Assert.Contains("200000000017", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordTooLargeExceptionReportsBothTheLimitAndTheOffset()
    {
        var ex = new RecordTooLargeException(4096, 65536);

        Assert.Equal(4096, ex.ByteOffset);
        Assert.Equal(65536, ex.MaxRecordLength);
        Assert.IsAssignableFrom<PunchIoException>(ex);
    }

    [Fact]
    public void EveryPunchIoExceptionIsAnIoException()
    {
        // Callers that already handle IOException keep working.
        Assert.IsAssignableFrom<IOException>(new PunchIoException("x", FileStatus.Ok));
    }
}
