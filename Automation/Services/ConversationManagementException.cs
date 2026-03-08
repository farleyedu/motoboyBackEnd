using System;

namespace APIBack.Automation.Services
{
    public class ConversationManagementException : Exception
    {
        public int StatusCode { get; }

        public ConversationManagementException(int statusCode, string message)
            : base(message)
        {
            StatusCode = statusCode;
        }
    }
}
