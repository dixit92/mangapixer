namespace com.lifepixer.mangaplex.Tray.Server;

/// <summary>
/// Result of validating a user-entered port string: either a parsed,
/// available <see cref="Port"/> or an <see cref="ErrorMessage"/> to show
/// inline in the dialog.
/// </summary>
public sealed record PortInputValidationResult(bool IsValid, int Port, string? ErrorMessage)
{
    public static PortInputValidationResult Success(int port) => new(true, port, null);

    public static PortInputValidationResult Failure(string message) => new(false, 0, message);
}

/// <summary>
/// Validates a candidate port typed into the "Set Port..." dialog: must
/// parse as an integer, fall inside the registerable-port range, and be free
/// right now. Kept separate from <see cref="ServerPortResolver"/> — that
/// class silently scans forward from a preferred port when the caller has no
/// opinion about which port it gets; this one is checking a port the user
/// explicitly asked for, so it must fail loudly instead of picking a
/// different one.
/// </summary>
public sealed class PortInputValidator
{
    public const int MinPort = 1024;
    public const int MaxPort = 65535;

    private readonly IPortAvailabilityChecker _checker;

    public PortInputValidator(IPortAvailabilityChecker? checker = null)
    {
        _checker = checker ?? new TcpPortAvailabilityChecker();
    }

    /// <param name="currentPort">
    /// The port the server is configured on right now, when known.
    /// Re-entering it is accepted WITHOUT the availability check — the
    /// running server legitimately holds that port, so the check would
    /// otherwise report the user's own unchanged choice as busy.
    /// </param>
    public PortInputValidationResult Validate(string input, int? currentPort = null)
    {
        if (!int.TryParse(input.Trim(), out var port))
            return PortInputValidationResult.Failure("Enter a whole number for the port.");

        if (port < MinPort || port > MaxPort)
            return PortInputValidationResult.Failure($"Port must be between {MinPort} and {MaxPort}.");

        if (port == currentPort)
            return PortInputValidationResult.Success(port);

        if (!_checker.IsPortAvailable(port))
            return PortInputValidationResult.Failure(
                $"Port {port} is not available right now. It may be in use by another "
                    + "program, or fall inside a range Windows has reserved for other services "
                    + "(Hyper-V, WSL, or Docker can reserve blocks of ports).");

        return PortInputValidationResult.Success(port);
    }
}
