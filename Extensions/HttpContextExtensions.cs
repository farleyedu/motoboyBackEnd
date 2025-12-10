using System;
using System.Collections.Generic;
using System.Linq;
using APIBack.Model.Auth;
using Microsoft.AspNetCore.Http;

namespace APIBack.Extensions
{
    public static class HttpContextExtensions
    {
        public static int? GetUserId(this HttpContext context)
        {
            if (context.Items.TryGetValue("UserId", out var value))
            {
                if (value is int intId)
                {
                    return intId;
                }

                if (int.TryParse(value?.ToString(), out var parsed))
                {
                    return parsed;
                }
            }

            return null;
        }

        public static string GetUserEmail(this HttpContext context)
        {
            return context.Items.TryGetValue("UserEmail", out var value)
                ? value?.ToString() ?? string.Empty
                : string.Empty;
        }

        public static string GetUserNome(this HttpContext context)
        {
            return context.Items.TryGetValue("UserNome", out var value)
                ? value?.ToString() ?? string.Empty
                : string.Empty;
        }

        public static bool IsSuperAdmin(this HttpContext context)
        {
            if (context.Items.TryGetValue("IsSuperAdmin", out var value) && value is bool b)
            {
                return b;
            }

            if (bool.TryParse(value?.ToString(), out var parsed))
            {
                return parsed;
            }

            return false;
        }

        public static Guid? GetEstabelecimentoId(this HttpContext context)
        {
            if (context.Items.TryGetValue("EstabelecimentoId", out var value))
            {
                if (value is Guid guid)
                {
                    return guid;
                }

                if (Guid.TryParse(value?.ToString(), out var parsed))
                {
                    return parsed;
                }
            }

            return null;
        }

        public static string GetEstabelecimentoNome(this HttpContext context)
        {
            return context.Items.TryGetValue("EstabelecimentoNome", out var value)
                ? value?.ToString() ?? string.Empty
                : string.Empty;
        }

        public static string GetTipoEstabelecimento(this HttpContext context)
        {
            return context.Items.TryGetValue("TipoEstabelecimento", out var value)
                ? value?.ToString() ?? string.Empty
                : string.Empty;
        }

        public static string GetTipoAcesso(this HttpContext context)
        {
            return context.Items.TryGetValue("TipoAcesso", out var value)
                ? value?.ToString() ?? string.Empty
                : string.Empty;
        }

        public static Guid? GetVinculoId(this HttpContext context)
        {
            if (context.Items.TryGetValue("VinculoId", out var value))
            {
                if (value is Guid guid)
                {
                    return guid;
                }

                if (Guid.TryParse(value?.ToString(), out var parsed))
                {
                    return parsed;
                }
            }

            return null;
        }

        public static Dictionary<string, List<string>> GetPermissoes(this HttpContext context)
        {
            if (context.Items.TryGetValue("Permissoes", out var value) &&
                value is Dictionary<string, List<string>> dict)
            {
                return dict;
            }

            return new Dictionary<string, List<string>>();
        }

        public static JwtPayload GetJwtPayload(this HttpContext context)
        {
            if (context.Items.TryGetValue("JwtPayload", out var value) && value is JwtPayload payload)
            {
                return payload;
            }

            return new JwtPayload();
        }

        public static bool TemPermissao(this HttpContext context, string modulo, string acao)
        {
            if (string.IsNullOrWhiteSpace(modulo) || string.IsNullOrWhiteSpace(acao))
            {
                return false;
            }

            var permissoes = context.GetPermissoes();

            if (!permissoes.TryGetValue(modulo, out var acoes) || acoes == null || acoes.Count == 0)
            {
                return false;
            }

            return acoes.Any(a =>
                !string.IsNullOrWhiteSpace(a) &&
                string.Equals(a, acao, StringComparison.OrdinalIgnoreCase));
        }
    }
}
