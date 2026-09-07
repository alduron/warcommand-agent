namespace WarCommand.Agent.Client.Http;

/// <summary>
/// The fragment the status strip shows for a refused call. Keyed on <c>code</c>, never on
/// <c>detail</c>, which is prose the server may reword.
/// </summary>
/// <remarks>
/// One word for every failure taught people to read the strip as noise: SUBMIT FAILED covers a
/// full board, a stale deployment and a malformed request, and the only way to tell them apart was
/// the log file the user does not have open. See Convention_WarCommandAgentCopyIsFragmentsNotSentences.
/// </remarks>
public static class FaultWords
{
    /// <summary>What the strip says when a submit is refused. Never for a transport failure.</summary>
    public static string Submit(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception is not WarCommandApiException api ? "SUBMIT FAILED" : api.Code switch
        {
            ErrorCodes.OpenRequestLimit => "TOO MANY OPEN, CLOSE ONE",
            ErrorCodes.PointCountMismatch => "WRONG POINT COUNT, TRY AGAIN",
            ErrorCodes.DeploymentMismatch => "WRONG DEPLOYMENT",
            ErrorCodes.DeploymentClosed => "DEPLOYMENT CLOSED",
            ErrorCodes.GroupFrozen => "GROUP FROZEN",
            ErrorCodes.RoleNotEnabled => "ROLE NOT ENABLED",
            ErrorCodes.VisitorNotPermitted => "VISITORS CANNOT",
            ErrorCodes.RateLimited => "SLOW DOWN",
            ErrorCodes.Unauthenticated => "SIGN IN AGAIN",
            ErrorCodes.Forbidden => "NOT ALLOWED",
            ErrorCodes.UnknownRequestType or ErrorCodes.UnknownSupplyKind
                or ErrorCodes.ValidationError => "SUBMIT REFUSED",
            _ => "SUBMIT FAILED",
        };
    }
}
