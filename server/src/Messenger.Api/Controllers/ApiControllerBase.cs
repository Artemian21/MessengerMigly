using MediatR;
using Messenger.Api.Infrastructure;
using Messenger.Domain.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Messenger.Api.Controllers;

[ApiController]
[Authorize]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
public abstract class ApiControllerBase(ISender mediator) : ControllerBase
{
    protected ISender Mediator { get; } = mediator;

    protected IActionResult Problem(Error error) => Problems.ToActionResult(error, HttpContext);

    protected async Task<IActionResult> Send<T>(IRequest<Result<T>> request, Func<T, IActionResult> onSuccess, CancellationToken ct)
    {
        var result = await Mediator.Send(request, ct);
        return result.IsSuccess ? onSuccess(result.Value) : Problem(result.Error!);
    }

    protected async Task<IActionResult> Send<T>(IRequest<Result<T>> request, CancellationToken ct) =>
        await Send(request, value => Ok(value), ct);

    protected async Task<IActionResult> Send(IRequest<Result> request, Func<IActionResult> onSuccess, CancellationToken ct)
    {
        var result = await Mediator.Send(request, ct);
        return result.IsSuccess ? onSuccess() : Problem(result.Error!);
    }

    protected Task<IActionResult> SendNoContent(IRequest<Result> request, CancellationToken ct) => Send(request, NoContent, ct);
}
