using System.Buffers;
using Microsoft.Extensions.Options;
using Sharpbill.Infrastructure.Configuration;

namespace Sharpbill.Api.Middleware;

public sealed class RequestBodyLimitMiddleware(RequestDelegate next, IOptions<SharpbillOptions> options)
{
    private const int BufferThresholdBytes = 64 * 1024;
    private const int ReadBufferBytes = 16 * 1024;
    private readonly long _limitBytes = options.Value.RequestPipeline.BodyLimitBytes;

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.Request.Path.StartsWithSegments("/api"))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        if (context.Request.ContentLength > _limitBytes)
        {
            throw new RequestBodyTooLargeException(_limitBytes);
        }

        context.Request.EnableBuffering(
            BufferThresholdBytes,
            checked(_limitBytes + 1));
        byte[] buffer = ArrayPool<byte>.Shared.Rent(ReadBufferBytes);
        try
        {
            long total = 0;
            while (true)
            {
                int maximumRead = (int)Math.Min(buffer.Length, (_limitBytes - total) + 1);
                int read = await context.Request.Body.ReadAsync(
                    buffer.AsMemory(0, maximumRead),
                    context.RequestAborted).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total += read;
                if (total > _limitBytes)
                {
                    throw new RequestBodyTooLargeException(_limitBytes);
                }
            }

            context.Request.Body.Position = 0;
            await next(context).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}

public sealed class RequestBodyTooLargeException(long limitBytes)
    : Exception($"The request body exceeded the configured {limitBytes}-byte limit.");
