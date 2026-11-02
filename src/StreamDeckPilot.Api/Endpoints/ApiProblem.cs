namespace StreamDeckPilot.Api.Endpoints;

/// <summary>
/// Every error the API returns is an RFC 7807 problem (<c>application/problem+json</c>) with
/// <c>status</c>, <c>title</c> and usually <c>detail</c>. Endpoint-specific data rides along as
/// extension members: <c>errors</c> (a list of messages) on validation failures, <c>error</c>
/// (a stable machine-readable code) where clients branch on it, <c>availablePages</c> on navigate.
/// </summary>
internal static class ApiProblem
{
    public static IResult BadRequest(string detail, IDictionary<string, object?>? extensions = null) =>
        Results.Problem(detail: detail, statusCode: StatusCodes.Status400BadRequest, extensions: extensions);

    /// <summary>
    /// A 400 carrying every validation message in <c>errors</c>; <c>detail</c> repeats the first
    /// so a client that only shows <c>detail</c> still says something useful.
    /// </summary>
    public static IResult Invalid(IReadOnlyList<string> errors, string title = "Invalid configuration") =>
        Results.Problem(
            title: title,
            detail: errors.Count == 1 ? errors[0] : $"{errors[0]} (and {errors.Count - 1} more)",
            statusCode: StatusCodes.Status400BadRequest,
            extensions: new Dictionary<string, object?> { ["errors"] = errors });

    public static IResult NotFound(string detail) =>
        Results.Problem(detail: detail, statusCode: StatusCodes.Status404NotFound);

    public static IResult TooLarge(string detail) =>
        Results.Problem(detail: detail, statusCode: StatusCodes.Status413PayloadTooLarge);

    public static IResult Conflict(string detail) =>
        Results.Problem(detail: detail, statusCode: StatusCodes.Status409Conflict);

    /// <summary>A 403 with a stable <c>error</c> code: the caller is known but may not do this.</summary>
    public static IResult Forbidden(string error, string detail) =>
        Results.Problem(detail: detail, statusCode: StatusCodes.Status403Forbidden,
            extensions: new Dictionary<string, object?> { ["error"] = error });

    public static IResult Unprocessable(string error, string detail) =>
        Results.Problem(detail: detail, statusCode: StatusCodes.Status422UnprocessableEntity,
            extensions: new Dictionary<string, object?> { ["error"] = error });
}
