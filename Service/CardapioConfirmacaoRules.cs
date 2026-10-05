using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace APIBack.Service
{
    /// <summary>Estados do pedido publico do cardapio web (coluna cardapio_pedido_publico.status).</summary>
    public static class CardapioPedidoStatus
    {
        /// <summary>Linhas antigas, anteriores a confirmacao pelo WhatsApp.</summary>
        public const string Pendente = "pendente";
        public const string AguardandoCodigo = "aguardando_codigo";
        public const string AguardandoAceite = "aguardando_aceite";
        public const string Aceito = "aceito";
        public const string Recusado = "recusado";
        public const string Expirado = "expirado";
    }

    public static class CardapioCanalConfirmacao
    {
        /// <summary>O cliente falou com a loja nas ultimas 24h: mandamos a mensagem e o pedido ja vai ao restaurante.</summary>
        public const string JanelaAberta = "janela_aberta";
        /// <summary>O cliente mandou o codigo de 4 digitos.</summary>
        public const string Codigo = "codigo";
    }

    /// <summary>Codigo achado numa mensagem. <see cref="Explicito"/> = o cliente escreveu a palavra "codigo".</summary>
    public readonly record struct CodigoExtraido(string Codigo, bool Explicito);

    /// <summary>
    /// Regras puras da confirmacao do pedido do cardapio web pelo WhatsApp: geracao e leitura do codigo,
    /// variantes do telefone e os textos enviados ao cliente.
    /// </summary>
    public static class CardapioConfirmacaoRules
    {
        public static readonly TimeSpan CodigoValidade = TimeSpan.FromMinutes(5);
        public const int MaxTentativasErradas = 5;
        public static readonly TimeSpan JanelaTentativas = TimeSpan.FromMinutes(15);
        public const int MaxMotivoRecusa = 200;
        private const int MaxTextoMensagem = 120;

        private static readonly Regex CodigoSolto = new(@"^\s*(\d{4})\s*[.!]?\s*$", RegexOptions.Compiled);
        private static readonly Regex CodigoComPalavra = new(@"\bcodigo\b[\s:#.\-]*(\d{4})\b", RegexOptions.Compiled);

        /// <summary>Quatro digitos, de 1000 a 9999 (sem zero a esquerda, mais facil de ler e digitar).</summary>
        public static string GerarCodigo(Func<int, int, int>? sortear = null) =>
            (sortear ?? ((minimo, maximo) => RandomNumberGenerator.GetInt32(minimo, maximo)))(1000, 10000)
                .ToString("D4", CultureInfo.InvariantCulture);

        /// <summary>
        /// Acha o codigo numa mensagem curta: ela inteira e so os 4 digitos, ou traz "codigo" e os 4 digitos
        /// ("Ola! Quero confirmar meu pedido. Codigo: 4821"). Frases longas com numeros nao contam.
        /// </summary>
        public static CodigoExtraido? ExtrairCodigo(string? texto)
        {
            if (string.IsNullOrWhiteSpace(texto) || texto.Length > MaxTextoMensagem) return null;

            var solto = CodigoSolto.Match(texto);
            if (solto.Success) return new CodigoExtraido(solto.Groups[1].Value, Explicito: false);

            var comPalavra = CodigoComPalavra.Match(SemAcentos(texto).ToLowerInvariant());
            return comPalavra.Success ? new CodigoExtraido(comPalavra.Groups[1].Value, Explicito: true) : null;
        }

        /// <summary>Texto que o link do WhatsApp deixa escrito para o cliente so apertar enviar.</summary>
        public static string MensagemDoLink(string codigo) => $"Olá! Quero confirmar meu pedido. Código: {codigo}";

        /// <summary>wa.me da loja com a mensagem pronta; nulo se a loja nao tem numero cadastrado.</summary>
        public static string? LinkWhatsapp(string? telefoneDaLoja, string codigo)
        {
            var digits = new string((telefoneDaLoja ?? string.Empty).Where(char.IsDigit).ToArray());
            if (digits.Length < 10) return null;
            if (digits.Length <= 11) digits = "55" + digits;
            return $"https://wa.me/{digits}?text={Uri.EscapeDataString(MensagemDoLink(codigo))}";
        }

        /// <summary>
        /// Digitos do telefone nas duas formas brasileiras (com e sem o nono digito), com o 55: o numero digitado
        /// e o guardado na conversa podem divergir so nisso. Vazio quando nao ha digitos suficientes.
        /// </summary>
        public static IReadOnlyList<string> VariantesTelefone(string? telefone)
        {
            var e164 = PhoneKey.ToE164(telefone);
            if (e164 == null) return Array.Empty<string>();

            var digits = e164[1..];
            var nacional = digits[2..];
            var variantes = new List<string> { digits };
            if (nacional.Length == 11 && nacional[2] == '9')
            {
                variantes.Add("55" + nacional[..2] + nacional[3..]);
            }
            else if (nacional.Length == 10 && nacional[2] is >= '6' and <= '9')
            {
                variantes.Add("55" + nacional[..2] + "9" + nacional[2..]);
            }
            return variantes;
        }

        public static string? NormalizarMotivo(string? motivo)
        {
            var trimmed = motivo?.Trim();
            if (string.IsNullOrEmpty(trimmed)) return null;
            return trimmed.Length > MaxMotivoRecusa ? trimmed[..MaxMotivoRecusa] : trimmed;
        }

        // ---- Textos enviados ao cliente --------------------------------------------------------------

        public static string LinhaDoItem(int quantidade, string nome, IEnumerable<string> adicionais)
        {
            var extras = adicionais.Where(a => !string.IsNullOrWhiteSpace(a)).ToList();
            return extras.Count == 0
                ? $"{quantidade}x {nome}"
                : $"{quantidade}x {nome} (+ {string.Join(", ", extras)})";
        }

        public static string CodigoInvalido() =>
            "Não encontrei esse código ou ele expirou. Volte ao cardápio e finalize o pedido de novo para gerar um código novo.";

        public static string CodigoDeOutroTelefone() =>
            "Este pedido foi iniciado com outro WhatsApp. Confirme usando o mesmo número informado no cardápio ou troque o WhatsApp do pedido para gerar um código novo.";

        public static string PedidoRecusado(string nomeCliente, string loja, string? motivo)
        {
            var porque = string.IsNullOrWhiteSpace(motivo) ? string.Empty : $" Motivo: {motivo.Trim()}.";
            return $"{PrimeiroNome(nomeCliente)}, infelizmente a {loja} não conseguiu aceitar seu pedido agora.{porque} Sentimos muito pelo transtorno.";
        }

        public static string Dinheiro(decimal valor)
        {
            // Sem depender da cultura pt-BR do servidor (container com globalizacao invariante): 1.234,56
            var texto = valor.ToString("N2", CultureInfo.InvariantCulture);
            return "R$ " + texto.Replace(',', '§').Replace('.', ',').Replace('§', '.');
        }

        private static string PrimeiroNome(string? nome)
        {
            var primeiro = (nome ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            return string.IsNullOrEmpty(primeiro) ? "tudo bem" : primeiro;
        }

        private static string SemAcentos(string texto)
        {
            var normalizado = texto.Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(normalizado.Length);
            foreach (var ch in normalizado)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) builder.Append(ch);
            }
            return builder.ToString().Normalize(NormalizationForm.FormC);
        }
    }
}
