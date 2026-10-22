using System;
using System.Collections.Generic;

namespace APIBack.DTOs.Cardapio
{
    public sealed class IfoodVinculoResumoDto
    {
        public bool Conectado { get; set; }
        public int Pendentes { get; set; }
    }

    public sealed class IfoodVinculoPendenteDto
    {
        public Guid VinculoId { get; set; }
        public string Nome { get; set; } = "";
        public string? ExternalCode { get; set; }
        public string? ImagemUrl { get; set; }
        public int Pedidos { get; set; }
        public DateTimeOffset UltimoVistoEm { get; set; }
        /// <summary>Verdadeiro quando uma pessoa desfez o vínculo: nunca é religado sozinho.</summary>
        public bool Desfeito { get; set; }
    }

    public sealed class IfoodVinculoPendentesDto
    {
        public List<IfoodVinculoPendenteDto> Itens { get; set; } = new();
        public int Total { get; set; }
    }

    public sealed class IfoodProdutoVinculoDto
    {
        public Guid VinculoId { get; set; }
        public string NomeIfood { get; set; } = "";
        public string? ExternalCode { get; set; }
        public string? ImagemUrl { get; set; }
        /// <summary>"nome" (casou sozinho pelo nome) ou "manual" (uma pessoa ligou).</summary>
        public string? Origem { get; set; }
        public DateTimeOffset? ConfirmadoEm { get; set; }
        public int? ConfirmadoPor { get; set; }
    }

    public sealed class IfoodProdutoVinculosDto
    {
        public List<IfoodProdutoVinculoDto> Vinculos { get; set; } = new();
    }

    public sealed class IfoodVincularRequest
    {
        public Guid? VinculoId { get; set; }
    }

    public sealed class IfoodVinculoDesfeitoDto
    {
        public Guid VinculoId { get; set; }
        public string Estado { get; set; } = "desfeito";
    }
}
