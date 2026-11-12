using System.Security.Claims;
using Novalist.Sync.Contracts;

namespace Novalist.Sync.Server.Access;

public static class AccountIdentityResetEndpoints
{
    public static IEndpointRouteBuilder MapAccountIdentityResetEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost(Protocol.AccountResetChallengePath, CreateAsync).RequireAuthorization();
        app.MapPost(Protocol.AccountResetPath, ResetAsync).RequireAuthorization();
        return app;
    }

    private static async Task<IResult> CreateAsync(ClaimsPrincipal principal, UserDirectory users, DeviceProofServer proofs,
        DeviceEnrollmentChallenges challenges, CancellationToken ct)
    {
        if (proofs.ServerUrl is null) return Results.Problem("Verified devices have not been configured.", statusCode: 503);
        try { return Results.Ok(challenges.CreateReset((await users.GetOrCreateAsync(principal, ct)).Id)); }
        catch (ConflictException error) { return Results.Problem(detail: error.Message, statusCode: 429); }
        catch (AccessDeniedException error) { return Results.Problem(detail: error.Message, statusCode: 403); }
    }

    private static async Task<IResult> ResetAsync(AccountIdentityResetRequest request, ClaimsPrincipal principal,
        DeviceProofServer proofs, AccountIdentityReset reset, CancellationToken ct)
    {
        if (proofs.ServerUrl is null) return Results.Problem("Verified devices have not been configured.", statusCode: 503);
        try { return Results.Ok(await reset.ResetAsync(principal, request, ct)); }
        catch (FormatException error) { return Results.Problem(detail: error.Message, statusCode: 400); }
        catch (AccessDeniedException error) { return Results.Problem(detail: error.Message, statusCode: 403); }
        catch (ConflictException error) { return Results.Problem(detail: error.Message, statusCode: 409); }
    }
}
