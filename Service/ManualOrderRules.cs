using System;
using System.Linq;
using APIBack.DTOs.Delivery;

namespace APIBack.Service
{
    /// <summary>Pedido manual validado e normalizado, pronto para gravar.</summary>
    public sealed record ManualOrder
    {
        public string NomeCliente { get; init; } = string.Empty;
        public string? TelefoneCliente { get; init; }
        public string EnderecoEntrega { get; init; } = string.Empty;
        public string Rua { get; init; } = string.Empty;
        public string Numero { get; init; } = string.Empty;
        public string Bairro { get; init; } = string.Empty;
        public string Cidade { get; init; } = string.Empty;
        public string? Estado { get; init; }
        public string? Cep { get; init; }
        public double Latitude { get; init; }
        public double Longitude { get; init; }
        public string? Items { get; init; }
        public decimal Value { get; init; }
        public string? TipoPagamento { get; init; }
        public decimal? Troco { get; init; }
        public string? Observacoes { get; init; }
        public int PrevisaoMinutos { get; init; }
        public string? CodigoEntrega { get; init; }

        // ---- Nucleo de pedido (Fase 2). Padroes = comportamento anterior (pedido manual pendente) ----
        public string Origem { get; init; } = APIBack.Model.Delivery.PedidoOrigem.Atendente;
        public string? OrigemRef { get; init; }
        public Guid? ConversaId { get; init; }
        /// <summary>Pedido em montagem (status Rascunho).</summary>
        public bool Rascunho { get; init; }
        /// <summary>Linhas precificadas pelo servidor; vazio = pedido no formato antigo (items texto + valor).</summary>
        public System.Collections.Generic.IReadOnlyList<PricedLine> Lines { get; init; } = System.Array.Empty<PricedLine>();
        public decimal? Subtotal { get; init; }
        public decimal? TaxaEntrega { get; init; }
        public decimal? Desconto { get; init; }
        /// <summary>Regras do estabelecimento que nao bloquearam.</summary>
        public System.Collections.Generic.IReadOnlyList<string> Avisos { get; init; } = System.Array.Empty<string>();

        /// <summary>Usa recursos que exigem as migracoes da Fase 2 (colunas novas e pedido_item).</summary>
        public bool NeedsCoreSchema => Lines.Count > 0 || OrigemRef != null || ConversaId != null || Rascunho
            || Origem != APIBack.Model.Delivery.PedidoOrigem.Atendente || Subtotal.HasValue || TaxaEntrega.HasValue;
    }

    /// <summary>
    /// Validacao do pedido criado pelo atendente. A coordenada e obrigatoria: ela vem da
    /// busca do endereco ou de um clique no mapa -- o sistema nunca inventa posicao.
    /// </summary>
    public static class ManualOrderRules
    {
        public const int DefaultPrevisaoMinutos = 40;

        public static ManualOrder Validate(CreatePedidoRequest? request)
        {
            if (request == null)
            {
                throw Invalid("Corpo da requisicao obrigatorio.");
            }

            var nome = Required(request.NomeCliente, "nome do cliente", 150);
            var rua = Required(request.Rua, "rua", 200);
            var numero = Required(request.Numero, "numero", 20);
            var bairro = Required(request.Bairro, "bairro", 100);
            var cidade = Required(request.Cidade, "cidade", 100);
            var complemento = Optional(request.Complemento, "complemento", 100);
            var telefone = Optional(request.TelefoneCliente, "telefone", 30);
            var estado = Optional(request.Estado, "estado", 2)?.ToUpperInvariant();
            if (estado != null && (estado.Length != 2 || !estado.All(char.IsLetter)))
            {
                throw Invalid("Estado deve ter 2 letras (ex.: MG).");
            }

            string? cep = null;
            if (!string.IsNullOrWhiteSpace(request.Cep))
            {
                var digits = new string(request.Cep.Where(char.IsDigit).ToArray());
                if (digits.Length != 8)
                {
                    throw Invalid("CEP deve ter 8 digitos.");
                }
                cep = $"{digits[..5]}-{digits[5..]}";
            }

            if (!request.Latitude.HasValue || !request.Longitude.HasValue)
            {
                throw Invalid("Localizacao obrigatoria: busque o endereco ou clique no mapa.");
            }
            var latitude = request.Latitude.Value;
            var longitude = request.Longitude.Value;
            if (!double.IsFinite(latitude) || latitude is < -90 or > 90
                || !double.IsFinite(longitude) || longitude is < -180 or > 180
                || (latitude == 0 && longitude == 0))
            {
                throw Invalid("Localizacao invalida.");
            }

            var value = request.Value ?? 0m;
            if (value < 0 || value > 100_000)
            {
                throw Invalid("Valor do pedido invalido.");
            }
            if (request.Troco is < 0 or > 100_000)
            {
                throw Invalid("Troco invalido.");
            }

            var previsao = request.PrevisaoMinutos ?? DefaultPrevisaoMinutos;
            if (previsao is < 1 or > 600)
            {
                throw Invalid("Previsao deve ficar entre 1 e 600 minutos.");
            }

            var endereco = complemento == null
                ? $"{rua}, {numero} – {bairro}"
                : $"{rua}, {numero} ({complemento}) – {bairro}";

            return new ManualOrder
            {
                NomeCliente = nome,
                TelefoneCliente = telefone,
                EnderecoEntrega = endereco,
                Rua = rua,
                Numero = numero,
                Bairro = bairro,
                Cidade = cidade,
                Estado = estado,
                Cep = cep,
                Latitude = latitude,
                Longitude = longitude,
                Items = Optional(request.Items, "itens", 2000),
                Value = decimal.Round(value, 2),
                TipoPagamento = Optional(request.TipoPagamento, "forma de pagamento", 50),
                Troco = request.Troco.HasValue ? decimal.Round(request.Troco.Value, 2) : null,
                Observacoes = Optional(request.Observacoes, "observacoes", 500),
                PrevisaoMinutos = previsao,
                CodigoEntrega = Optional(request.CodigoEntrega, "codigo de entrega", 20)
            };
        }

        private static string Required(string? value, string label, int max) =>
            Optional(value, label, max) ?? throw Invalid($"Informe {label}.");

        private static string? Optional(string? value, string label, int max)
        {
            var trimmed = value?.Trim();
            if (string.IsNullOrEmpty(trimmed)) return null;
            if (trimmed.Length > max)
            {
                throw Invalid($"Campo {label} excede {max} caracteres.");
            }
            return trimmed;
        }

        private static DeliveryDomainException Invalid(string message) =>
            new(422, "INVALID_ORDER", message);
    }
}
