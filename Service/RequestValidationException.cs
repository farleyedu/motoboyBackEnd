using System;
using System.Collections.Generic;

namespace APIBack.Service
{
    public class RequestValidationException : Exception
    {
        public RequestValidationException(string message, Dictionary<string, List<string>> errors)
            : base(message)
        {
            Errors = errors ?? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        }

        public Dictionary<string, List<string>> Errors { get; }
    }
}
