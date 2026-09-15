namespace com.lifepixer.mangaplex.Tests.Tray.Server;

using com.lifepixer.mangaplex.Tray.Server;
using Xunit;

public sealed class PortInputValidatorTests
{
    private sealed class FakePortAvailabilityChecker(params int[] availablePorts) : IPortAvailabilityChecker
    {
        private readonly HashSet<int> _available = [.. availablePorts];

        public bool IsPortAvailable(int port) => _available.Contains(port);
    }

    [Fact]
    public void Validate_WhenPortIsFreeAndInRange_Succeeds()
    {
        var validator = new PortInputValidator(new FakePortAvailabilityChecker(27273));

        var result = validator.Validate("27273");

        Assert.True(result.IsValid);
        Assert.Equal(27273, result.Port);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public void Validate_TrimsWhitespaceAroundTheInput()
    {
        var validator = new PortInputValidator(new FakePortAvailabilityChecker(27273));

        var result = validator.Validate("  27273  ");

        Assert.True(result.IsValid);
        Assert.Equal(27273, result.Port);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a number")]
    [InlineData("27273.5")]
    public void Validate_WhenInputIsNotAnInteger_Fails(string input)
    {
        var validator = new PortInputValidator(new FakePortAvailabilityChecker());

        var result = validator.Validate(input);

        Assert.False(result.IsValid);
        Assert.NotNull(result.ErrorMessage);
    }

    [Theory]
    [InlineData(1023)]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void Validate_WhenPortIsOutsideTheRegisterableRange_Fails(int port)
    {
        var validator = new PortInputValidator(new FakePortAvailabilityChecker(port));

        var result = validator.Validate(port.ToString());

        Assert.False(result.IsValid);
        Assert.Contains("between", result.ErrorMessage);
    }

    [Theory]
    [InlineData(1024)]
    [InlineData(65535)]
    public void Validate_AcceptsTheRegisterableRangeBoundaries(int port)
    {
        var validator = new PortInputValidator(new FakePortAvailabilityChecker(port));

        var result = validator.Validate(port.ToString());

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_WhenPortEqualsTheCurrentPort_SucceedsWithoutAvailabilityCheck()
    {
        // The running server legitimately holds the current port, so the
        // availability check would report the user's own unchanged choice
        // as busy - re-confirming it must be accepted, not rejected.
        var validator = new PortInputValidator(new FakePortAvailabilityChecker());

        var result = validator.Validate("27272", currentPort: 27272);

        Assert.True(result.IsValid);
        Assert.Equal(27272, result.Port);
    }

    [Fact]
    public void Validate_WhenPortIsInRangeButUnavailable_FailsWithAnInlineMessage()
    {
        var validator = new PortInputValidator(new FakePortAvailabilityChecker());

        var result = validator.Validate("27273");

        Assert.False(result.IsValid);
        Assert.Contains("27273", result.ErrorMessage);
        Assert.Contains("Windows", result.ErrorMessage);
    }
}
