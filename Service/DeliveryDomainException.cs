using System;

namespace APIBack.Service
{
    public sealed class DeliveryDomainException : Exception
    {
        public DeliveryDomainException(int statusCode, string code, string message, object? details = null)
            : base(message)
        {
            StatusCode = statusCode;
            Code = code;
            Details = details;
        }

        public int StatusCode { get; }
        public string Code { get; }
        public object? Details { get; }
    }
}

