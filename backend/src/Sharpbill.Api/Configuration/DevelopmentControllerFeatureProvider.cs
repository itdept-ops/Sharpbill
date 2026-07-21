using Microsoft.AspNetCore.Mvc.Controllers;

namespace Sharpbill.Api.Configuration;

public sealed class DevelopmentControllerFeatureProvider(bool enabled)
    : ControllerFeatureProvider
{
    protected override bool IsController(System.Reflection.TypeInfo typeInfo) =>
        base.IsController(typeInfo) &&
        (enabled || typeInfo.AsType() != typeof(Controllers.DevelopmentAuthController));
}
