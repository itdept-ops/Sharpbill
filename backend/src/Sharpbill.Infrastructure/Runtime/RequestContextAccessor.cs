using Sharpbill.Application.Common;

namespace Sharpbill.Infrastructure.Runtime;

public sealed class RequestContextAccessor : IRequestContextAccessor
{
    public RequestContext Current { get; set; } = new();
}
