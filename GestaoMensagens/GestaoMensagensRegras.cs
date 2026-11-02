using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace APIBack.GestaoMensagens
{
    /// <summary>Regras puras: telefone, janela de 24 h por numero e contato, variaveis de modelo e ordem dos recibos.</summary>
    public static class GestaoMensagensRegras
    {
        public static readonly TimeSpan Janela = TimeSpan.FromHours(24);
        private static readonly Regex E164 = new(@"^\+[1-9][0-9]{7,14}$", RegexOptions.Compiled);
        private static readonly Regex Variavel = new(@"\{\{\s*(\d+)\s*\}\}", RegexOptions.Compiled);

        public static bool TelefoneValido(string? e164) => e164 != null && E164.IsMatch(e164);

        /// <summary>
        /// Chave de comparacao entre o telefone gravado e o remetente que a Meta devolve (so digitos, com DDI 55). O celular antigo sem o
        /// nono digito e o mesmo numero: 55 + DDD + 9 + 8 digitos vira 55 + DDD + 8 digitos.
        /// </summary>
        public static string? ChaveTelefone(string? telefone)
        {
            var d = new string((telefone ?? string.Empty).Where(char.IsDigit).ToArray());
            if (d.Length is 10 or 11) d = "55" + d;
            if (d.Length < 10) return null;
            if (d.StartsWith("55") && d.Length == 13 && d[4] == '9') d = d[..4] + d[5..];
            return d;
        }

        /// <summary>Janela aberta ate (24 h depois da ultima mensagem recebida DAQUELE numero DAQUELE contato). Nulo = fechada.</summary>
        public static DateTimeOffset? JanelaAte(DateTimeOffset? ultimaRecebida, DateTimeOffset agora)
        {
            if (!ultimaRecebida.HasValue) return null;
            var fim = ultimaRecebida.Value + Janela;
            return fim > agora ? fim : null;
        }

        public static int ContarVariaveis(string? corpo) =>
            string.IsNullOrEmpty(corpo) ? 0 : Variavel.Matches(corpo).Select(m => int.Parse(m.Groups[1].Value)).DefaultIfEmpty(0).Max();

        /// <summary>Texto com as variaveis preenchidas, para o historico.</summary>
        public static string Preencher(string? corpo, IReadOnlyList<string> variaveis) =>
            Variavel.Replace(corpo ?? string.Empty, m =>
            {
                var i = int.Parse(m.Groups[1].Value) - 1;
                return i >= 0 && i < variaveis.Count ? variaveis[i] : m.Value;
            });

        /// <summary>Mesmas exigencias da Meta para parametros: todos preenchidos, sem quebra de linha, tabulacao ou 4+ espacos seguidos.</summary>
        public static string? ErroVariaveis(int esperadas, IReadOnlyList<string>? variaveis)
        {
            var v = variaveis ?? Array.Empty<string>();
            if (v.Count != esperadas) return $"O modelo tem {esperadas} variavel(is) e vieram {v.Count}.";
            for (var i = 0; i < v.Count; i++)
            {
                var t = v[i];
                if (string.IsNullOrWhiteSpace(t)) return $"A variavel {i + 1} esta vazia.";
                if (t.Length > 1024) return $"A variavel {i + 1} passa de 1024 caracteres.";
                if (t.Contains('\n') || t.Contains('\r') || t.Contains('\t') || t.Contains("    "))
                    return $"A variavel {i + 1} nao pode ter quebra de linha, tabulacao nem 4 espacos seguidos.";
            }
            return null;
        }

        private static int Ordem(string estado) => estado switch
        {
            EstadoMensagem.Enviando => 0,
            EstadoMensagem.Enviado => 1,
            EstadoMensagem.Entregue => 2,
            EstadoMensagem.Lido => 3,
            _ => -1
        };

        /// <summary>
        /// Estado depois de um recibo da Meta; nulo = nada a mudar. Recibo fora de ordem nunca regride (lido nao volta a entregue) e falha so
        /// vale antes de entregue. Estados que nao vem da Meta (por fora, aguardando confirmacao, recebida) nunca mudam por recibo.
        /// </summary>
        public static string? AplicarRecibo(string estadoAtual, string? statusMeta)
        {
            var novo = (statusMeta ?? string.Empty).ToLowerInvariant() switch
            {
                "sent" => EstadoMensagem.Enviado,
                "delivered" => EstadoMensagem.Entregue,
                "read" => EstadoMensagem.Lido,
                "failed" => EstadoMensagem.Falhou,
                _ => null
            };
            if (novo == null) return null;
            if (estadoAtual == EstadoMensagem.Falhou) return null;
            var atual = Ordem(estadoAtual);
            if (atual < 0) return null;
            if (novo == EstadoMensagem.Falhou) return atual >= Ordem(EstadoMensagem.Entregue) ? null : novo;
            return Ordem(novo) > atual ? novo : null;
        }

        /// <summary>Abre o WhatsApp da pessoa com o texto pronto. Nao envia nada.</summary>
        public static string LinkWhatsapp(string telefoneE164, string texto) =>
            $"https://wa.me/{new string(telefoneE164.Where(char.IsDigit).ToArray())}?text={Uri.EscapeDataString(texto)}";
    }
}
