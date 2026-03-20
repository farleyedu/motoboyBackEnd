using System;
using System.Collections.Generic;

namespace APIBack.Service
{
    public class AdminUsuarioValidationException : RequestValidationException
    {
        public AdminUsuarioValidationException(string message, Dictionary<string, List<string>> errors)
            : base(message, errors)
        {
        }
    }
}
