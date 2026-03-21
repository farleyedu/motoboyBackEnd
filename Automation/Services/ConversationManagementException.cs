using System;

namespace APIBack.Automation.Services
{
    public class ConversationManagementException : Exception
    {
        public int StatusCode { get; }
        public string? Code { get; }

        public ConversationManagementException(int statusCode, string message)
            : base(message)
        {
            StatusCode = statusCode;
        }

        public ConversationManagementException(int statusCode, string message, string? code)
            : base(message)
        {
            StatusCode = statusCode;
            Code = string.IsNullOrWhiteSpace(code) ? null : code.Trim();
        }
    }
}
