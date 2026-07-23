using System;
using APIBack.DTOs.Common;
using APIBack.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace APIBack.Attributes
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
    public sealed class RequireOperationalSessionAttribute : Attribute, IAuthorizationFilter
    {
        public void OnAuthorization(AuthorizationFilterContext context)
        {
            var payload = context.HttpContext.GetJwtPayload();
            if (!string.Equals(payload.TokenUse, "delivery_operational", StringComparison.Ordinal) ||
                !payload.MotoboySessionId.HasValue ||
                !payload.MotoboyId.HasValue ||
                !payload.SessionEpoch.HasValue)
            {
                context.Result = new JsonResult(ApiResponse<object>.Fail(
                    "Token operacional obrigatorio.",
                    "OPERATIONAL_TOKEN_REQUIRED"))
                {
                    StatusCode = 401
                };
            }
        }
    }
}

