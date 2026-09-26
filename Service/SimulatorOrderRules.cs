using System;
using System.Collections.Generic;
using System.Linq;
using APIBack.DTOs.Delivery;

namespace APIBack.Service
{
    /// <summary>Um campo a gravar: coluna do banco e o valor ja normalizado (null limpa).</summary>
    public sealed record SimulatorColumn(string Column, object? Value);

    /// <summary>Alteracao validada de um pedido pelo simulador, pronta para virar UPDATE.</summary>
    public sealed class SimulatorOrderPatch
    {
        public List<SimulatorColumn> Columns { get; } = new();
        /// <summary>Partes do endereco: se qualquer uma mudar, o endereco_entrega e recomposto.</summary>
        public string? Rua { get; init; }
        public string? Numero { get; init; }
        public string? Complemento { get; init; }
        public string? Bairro { get; init; }
        public bool TouchesAddress { get; init; }
        public int? PrevisaoEmMinutos { get; init; }
        public int? PedidoHaMinutos { get; init; }
        public bool IsEmpty => Columns.Count == 0 && PrevisaoEmMinutos is null && PedidoHaMinutos is null && !TouchesAddress;
    }

    public static class SimulatorOrderRules
    {
        /// <summary>Uma semana para frente, e ate 5 anos para tras (pedido esquecido de anos).</summary>
        public const int MaxFutureMinutes = 7 * 24 * 60;
        public const int MaxPastMinutes = 5 * 365 * 24 * 60;

        /// <summary>
        /// So pedido que ja acabou (concluido/cancelado) pode ser reaberto no simulador, e volta a pendente.
        /// Atribuido e em rota exigem motoboy + parada de rota: nao se chega a eles por edicao, so pelos
        /// comandos de fila, que criam os dois juntos.
        /// </summary>
        public static bool CanReopen(APIBack.Model.Enum.StatusPedido status) =>
            status == APIBack.Model.Enum.StatusPedido.Concluido || status == APIBack.Model.Enum.StatusPedido.Cancelado;

        public static SimulatorOrderPatch Validate(SimulatorPedidoRequest? request)
        {
            if (request == null) throw Invalid("Corpo da requisicao obrigatorio.");

            var rua = Text(request.Rua, "rua", 200);
            var numero = Text(request.Numero, "numero", 20);
            var bairro = Text(request.Bairro, "bairro", 100);
            var complemento = Text(request.Complemento, "complemento", 100);
            var touchesAddress = rua.Set || numero.Set || bairro.Set || complemento.Set;

            var patch = new SimulatorOrderPatch
            {
                Rua = rua.Value, Numero = numero.Value, Bairro = bairro.Value, Complemento = complemento.Value,
                TouchesAddress = touchesAddress,
                PrevisaoEmMinutos = Minutes(request.PrevisaoEmMinutos, "previsao"),
                PedidoHaMinutos = request.PedidoHaMinutos is null ? null : Past(request.PedidoHaMinutos.Value),
            };

            void Add(string column, (bool Set, string? Value) field) { if (field.Set) patch.Columns.Add(new SimulatorColumn(column, field.Value)); }

            Add("nome_cliente", Text(request.NomeCliente, "nome do cliente", 150));
            Add("telefone_cliente", Text(request.TelefoneCliente, "telefone", 30));
            Add("entrega_rua", rua);
            Add("entrega_numero", numero);
            Add("entrega_bairro", bairro);
            // A coluna "region" e o bairro que a tela mostra: acompanha.
            Add("region", bairro);
            Add("entrega_cidade", Text(request.Cidade, "cidade", 100));

            var estado = Text(request.Estado, "estado", 2);
            if (estado.Set)
            {
                var uf = estado.Value?.ToUpperInvariant();
                if (uf != null && (uf.Length != 2 || !uf.All(char.IsLetter))) throw Invalid("Estado deve ter 2 letras (ex.: MG).");
                patch.Columns.Add(new SimulatorColumn("entrega_estado", uf));
            }

            var cep = Text(request.Cep, "CEP", 20);
            if (cep.Set)
            {
                if (cep.Value == null)
                {
                    patch.Columns.Add(new SimulatorColumn("entrega_cep", null));
                }
                else
                {
                    var digits = new string(cep.Value.Where(char.IsDigit).ToArray());
                    if (digits.Length != 8) throw Invalid("CEP deve ter 8 digitos.");
                    patch.Columns.Add(new SimulatorColumn("entrega_cep", $"{digits[..5]}-{digits[5..]}"));
                }
            }

            if (request.Latitude.HasValue != request.Longitude.HasValue)
            {
                throw Invalid("Informe latitude e longitude juntas.");
            }
            if (request.Latitude.HasValue)
            {
                var lat = request.Latitude.Value;
                var lng = request.Longitude!.Value;
                if (!double.IsFinite(lat) || lat is < -90 or > 90 || !double.IsFinite(lng) || lng is < -180 or > 180 || (lat == 0 && lng == 0))
                {
                    throw Invalid("Localizacao invalida.");
                }
                patch.Columns.Add(new SimulatorColumn("latitude", lat));
                patch.Columns.Add(new SimulatorColumn("longitude", lng));
            }

            Add("items", Text(request.Items, "itens", 4000));

            if (request.Value.HasValue)
            {
                if (request.Value < 0 || request.Value > 100_000) throw Invalid("Valor do pedido invalido.");
                patch.Columns.Add(new SimulatorColumn("value", decimal.Round(request.Value.Value, 2)));
            }

            Add("tipo_pagamento", Text(request.TipoPagamento, "forma de pagamento", 50));
            Add("status_pagamento", Text(request.StatusPagamento, "status do pagamento", 50));

            if (request.Troco.HasValue)
            {
                if (request.Troco < 0 || request.Troco > 100_000) throw Invalid("Troco invalido.");
                patch.Columns.Add(new SimulatorColumn("troco", request.Troco == 0 ? null : decimal.Round(request.Troco.Value, 2)));
            }

            Add("observacoes", Text(request.Observacoes, "observacoes", 500));
            Add("codigo_entrega", Text(request.CodigoEntrega, "codigo de entrega", 20));
            return patch;
        }

        /// <summary>Endereco de exibicao, no mesmo formato do pedido manual.</summary>
        public static string ComposeAddress(string? rua, string? numero, string? complemento, string? bairro)
        {
            var street = string.Join(", ", new[] { rua, numero }.Where(part => !string.IsNullOrWhiteSpace(part)));
            if (!string.IsNullOrWhiteSpace(complemento)) street += $" ({complemento})";
            return string.IsNullOrWhiteSpace(bairro) ? street : $"{street} – {bairro}";
        }

        private static int? Minutes(int? value, string field)
        {
            if (value is null) return null;
            if (value > MaxFutureMinutes || value < -MaxPastMinutes)
            {
                throw Invalid($"Valor de {field} fora do limite (ate {MaxFutureMinutes} min a frente ou {MaxPastMinutes} min atras).");
            }
            return value;
        }

        private static int? Past(int minutesAgo)
        {
            if (minutesAgo < 0 || minutesAgo > MaxPastMinutes) throw Invalid("O pedido so pode ter sido feito no passado (minutos de 0 ao limite).");
            return minutesAgo;
        }

        /// <summary>Nulo = manter; vazio = limpar (Value nulo com Set verdadeiro).</summary>
        private static (bool Set, string? Value) Text(string? value, string label, int max)
        {
            if (value is null) return (false, null);
            var trimmed = value.Trim();
            if (trimmed.Length == 0) return (true, null);
            if (trimmed.Length > max) throw Invalid($"Campo {label} excede {max} caracteres.");
            return (true, trimmed);
        }

        private static DeliveryDomainException Invalid(string message) => new(422, "INVALID_ORDER", message);
    }
}
