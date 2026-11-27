using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;

namespace APIBack.Controllers
{
    /// <summary>
    /// Teto de chamadas das rotas publicas do cardapio. Atras do proxy da hospedagem todas as conexoes chegam do mesmo
    /// endereco: contar so por ele faria um cliente bloquear todos os outros. Quando a conexao vem de um proxy (rede
    /// privada), o teto e por cliente (primeiro endereco do X-Forwarded-For) com um teto geral maior por proxy, porque o
    /// cabecalho pode ser forjado. Conexao direta: o cabecalho e ignorado.
    /// </summary>
    internal static class LimitePublicoDoCardapio
    {
        public const int FolgaDoProxy = 10;

        public static bool Permitir(HttpContext? context, IMemoryCache cache, string rota, int maximo, TimeSpan janela)
        {
            var conexao = context?.Connection.RemoteIpAddress;
            var encaminhado = EnderecoEncaminhado(context, conexao);
            if (encaminhado == null)
            {
                return Contar(cache, $"{rota}:{conexao?.ToString() ?? "desconhecido"}", janela) <= maximo;
            }

            // O proprio cliente estoura primeiro: so quem passou no teto dele gasta o teto geral do proxy.
            return Contar(cache, $"{rota}:cliente:{encaminhado}", janela) <= maximo
                && Contar(cache, $"{rota}:proxy:{conexao}", janela) <= maximo * FolgaDoProxy;
        }

        internal static string? EnderecoEncaminhado(HttpContext? context, IPAddress? conexao)
        {
            if (context == null || conexao == null || !RedePrivada(conexao)) return null;
            var primeiro = context.Request.Headers["X-Forwarded-For"].ToString().Split(',').FirstOrDefault()?.Trim();
            return IPAddress.TryParse(primeiro, out var cliente) ? cliente.ToString() : null;
        }

        private static bool RedePrivada(IPAddress endereco)
        {
            var ip = endereco.IsIPv4MappedToIPv6 ? endereco.MapToIPv4() : endereco;
            if (IPAddress.IsLoopback(ip)) return true;
            if (ip.AddressFamily == AddressFamily.InterNetworkV6) return ip.IsIPv6LinkLocal || (ip.GetAddressBytes()[0] & 0xFE) == 0xFC;

            var b = ip.GetAddressBytes();
            // 10/8, 172.16/12, 192.168/16 e 100.64/10 (rede interna de provedores de hospedagem).
            return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168)
                || (b[0] == 100 && b[1] is >= 64 and <= 127);
        }

        private static int Contar(IMemoryCache cache, string chave, TimeSpan janela)
        {
            var usadas = cache.GetOrCreate(chave, entrada =>
            {
                entrada.AbsoluteExpirationRelativeToNow = janela;
                return new int[1];
            })!;
            return Interlocked.Increment(ref usadas[0]);
        }
    }
}
