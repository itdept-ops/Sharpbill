namespace Sharpbill.Application.Abstractions;

public interface ITransactionExecutor
{
    Task<T> ExecuteTransactionAsync<T>(
        IUnitOfWork unitOfWork,
        string operationName,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken);

    Task ExecuteTransactionAsync(
        IUnitOfWork unitOfWork,
        string operationName,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken);
}
