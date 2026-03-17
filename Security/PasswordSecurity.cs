using System;

namespace APIBack.Security
{
    public static class PasswordSecurity
    {
        public static string Hash(string rawPassword)
        {
            if (string.IsNullOrWhiteSpace(rawPassword))
            {
                throw new ArgumentException("Senha nao pode ser vazia.", nameof(rawPassword));
            }

            return BCrypt.Net.BCrypt.HashPassword(rawPassword.Trim());
        }

        public static bool IsHash(string? storedPassword)
        {
            if (string.IsNullOrWhiteSpace(storedPassword))
            {
                return false;
            }

            return storedPassword.StartsWith("$2a$", StringComparison.Ordinal)
                || storedPassword.StartsWith("$2b$", StringComparison.Ordinal)
                || storedPassword.StartsWith("$2y$", StringComparison.Ordinal);
        }

        public static bool Verify(string rawPassword, string? storedPassword)
        {
            if (string.IsNullOrWhiteSpace(rawPassword) || string.IsNullOrWhiteSpace(storedPassword))
            {
                return false;
            }

            if (!IsHash(storedPassword))
            {
                return string.Equals(rawPassword, storedPassword, StringComparison.Ordinal);
            }

            return BCrypt.Net.BCrypt.Verify(rawPassword, storedPassword);
        }
    }
}
