using PS5LM.Companion.Core.Abstractions;
using PS5LM.Companion.Core.Models;
using PS5LM.Companion.Core.Transfers;

namespace PS5LM.Companion.App.Endpoints;

/// <summary>The body of every non-2xx answer.</summary>
public sealed record ApiError(string Message);

internal static class ApiResults
{
    public static IResult From(OperationResult result) =>
        Results.Json(
            result.Succeeded ? new { message = result.Message } : new ApiError(result.Message ?? result.Outcome.ToString()),
            statusCode: result.Outcome switch
            {
                OperationOutcome.Accepted => StatusCodes.Status202Accepted,
                OperationOutcome.NoChange => StatusCodes.Status200OK,
                OperationOutcome.NotFound => StatusCodes.Status404NotFound,
                OperationOutcome.Conflict => StatusCodes.Status409Conflict,
                OperationOutcome.Unavailable => StatusCodes.Status503ServiceUnavailable,
                _ => StatusCodes.Status400BadRequest,
            });

    public static IResult Error(string message, int statusCode) => Results.Json(new ApiError(message), statusCode: statusCode);

    /// <summary>Runs a console or hub operation and maps the two expected failures to HTTP statuses.</summary>
    public static async Task<IResult> GuardAsync(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (ConsoleUnreachableException ex)
        {
            return Error(ex.Message, StatusCodes.Status503ServiceUnavailable);
        }
        catch (HubUnavailableException ex)
        {
            return Error(ex.Message, StatusCodes.Status502BadGateway);
        }
    }
}
