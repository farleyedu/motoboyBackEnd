using System.Collections.Generic;
using APIBack.DTOs.Common;
using APIBack.Service;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers
{
    public abstract class ApiControllerBase : ControllerBase
    {
        protected UnprocessableEntityObjectResult ValidationErrorResponse(RequestValidationException ex)
        {
            return UnprocessableEntity(new ApiErrorResponse
            {
                Error = ex.Message,
                FieldErrors = ex.Errors,
                TraceId = HttpContext.TraceIdentifier
            });
        }

        protected BadRequestObjectResult BadRequestErrorResponse(string message, Dictionary<string, List<string>>? errors = null)
        {
            return BadRequest(new ApiErrorResponse
            {
                Error = message,
                FieldErrors = errors,
                TraceId = HttpContext.TraceIdentifier
            });
        }

        protected NotFoundObjectResult NotFoundErrorResponse(string message, Dictionary<string, List<string>>? errors = null)
        {
            return NotFound(new ApiErrorResponse
            {
                Error = message,
                FieldErrors = errors,
                TraceId = HttpContext.TraceIdentifier
            });
        }

        protected ObjectResult ConflictErrorResponse(string message, Dictionary<string, List<string>>? errors = null)
        {
            return StatusCode(StatusCodes.Status409Conflict, new ApiErrorResponse
            {
                Error = message,
                FieldErrors = errors,
                TraceId = HttpContext.TraceIdentifier
            });
        }
    }
}
