namespace Messenger.Domain.Common;

public enum ErrorType
{
    Validation,
    Unauthorized,
    Forbidden,
    NotFound,
    Conflict,
    Gone,
    BadRequest,
    Locked,
}

/// <summary>A typed, expected failure. Unexpected failures are exceptions.</summary>
public sealed record Error(ErrorType Type, string Code, string Message, IReadOnlyDictionary<string, string[]>? Fields = null)
{
    public static Error Validation(string field, string message) =>
        new(ErrorType.Validation, "validation", "One or more validation errors occurred.",
            new Dictionary<string, string[]> { [field] = [message] });

    public static Error NotFound(string what) => new(ErrorType.NotFound, "not_found", $"{what} was not found.");
    public static Error Forbidden(string message = "You are not allowed to perform this action.") =>
        new(ErrorType.Forbidden, "forbidden", message);
    public static Error Conflict(string code, string message) => new(ErrorType.Conflict, code, message);
    public static Error Unauthorized(string message = "Authentication is required.") =>
        new(ErrorType.Unauthorized, "unauthorized", message);
    public static Error Gone(string code, string message) => new(ErrorType.Gone, code, message);
    public static Error BadRequest(string code, string message) => new(ErrorType.BadRequest, code, message);
}

public interface IResult
{
    bool IsSuccess { get; }
    Error? Error { get; }
}

public class Result : IResult
{
    protected Result(Error? error) => Error = error;

    public bool IsSuccess => Error is null;
    public Error? Error { get; }

    public static Result Success() => new(null);
    public static Result Failure(Error error) => new(error);
    public static Result<T> Success<T>(T value) => Result<T>.Success(value);

    public static implicit operator Result(Error error) => Failure(error);
}

public sealed class Result<T> : Result
{
    private readonly T? _value;

    private Result(T? value, Error? error) : base(error) => _value = value;

    public T Value => IsSuccess ? _value! : throw new InvalidOperationException("Result has no value.");

    public static Result<T> Success(T value) => new(value, null);
    public static new Result<T> Failure(Error error) => new(default, error);

    public static implicit operator Result<T>(T value) => Success(value);
    public static implicit operator Result<T>(Error error) => Failure(error);
}
