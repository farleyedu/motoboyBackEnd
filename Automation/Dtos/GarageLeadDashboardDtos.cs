using System;
using System.Collections.Generic;

namespace APIBack.Automation.Dtos
{
    public class GarageLeadListItemDto
    {
        public Guid Id { get; set; }
        public string Telefone { get; set; } = string.Empty;
        public string Nome { get; set; } = string.Empty;
        public string? Cpf { get; set; }
        public string Objetivo { get; set; } = string.Empty;
        public string Modelo { get; set; } = string.Empty;
        public string? CarroInteresse { get; set; }
        public string? EstabelecimentoSlug { get; set; }
        public Guid? IdVeiculoSelecionado { get; set; }
        public string? VeiculoSelecionadoTitulo { get; set; }
        public string? VeiculoSelecionadoSlug { get; set; }
        public string? VeiculoSelecionadoUrl { get; set; }
        public string? Faixa { get; set; }
        public string? Pagamento { get; set; }
        public string? Entrada { get; set; }
        public string? Urgencia { get; set; }
        public string Status { get; set; } = string.Empty;
        public DateTime Data { get; set; }
        public int SimulacoesCount { get; set; }
    }

    public class GarageLeadDetailDto : GarageLeadListItemDto
    {
        public Guid IdConversa { get; set; }
        public Guid IdCliente { get; set; }
        public bool ViaNumeroCentral { get; set; }
        public DateTime DataCriacao { get; set; }
        public DateTime DataAtualizacao { get; set; }
        public DateTime? DataConclusao { get; set; }

        public string? ModeloInteresse { get; set; }
        public string? FaixaInvestimento { get; set; }
        public string? FormaPagamento { get; set; }
        public string? ValorEntradaTexto { get; set; }

        public string? TrocaModeloAtual { get; set; }
        public string? TrocaAnoModelo { get; set; }
        public string? TrocaKm { get; set; }
        public string? TrocaQuitado { get; set; }
        public string? TrocaModeloDesejado { get; set; }
        public string? TrocaCondicao { get; set; }

        public string? VendaModelo { get; set; }
        public string? VendaAno { get; set; }
        public string? VendaKm { get; set; }
        public string? VendaQuitado { get; set; }

        public List<GarageLeadSimulationDto> Simulacoes { get; set; } = new();
    }

    public class GarageLeadSimulationDto
    {
        public Guid Id { get; set; }
        public string Titulo { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string? TipoSimulacao { get; set; }
        public string? Comentario { get; set; }
        public string? Valor { get; set; }
        public string? VeiculoMarca { get; set; }
        public string? VeiculoModelo { get; set; }
        public string? VeiculoVersao { get; set; }
        public int? VeiculoAno { get; set; }
        public int? VeiculoKm { get; set; }
        public decimal? VeiculoValor { get; set; }
        public decimal? EntradaValor { get; set; }
        public decimal? SaldoFinanciado { get; set; }
        public int? ParcelasQuantidade { get; set; }
        public decimal? ParcelaValor { get; set; }
        public decimal? TaxaJurosMensal { get; set; }
        public string? Observacoes { get; set; }
        public DateTime? ValidadeEm { get; set; }
        public int? CriadoPorUsuarioId { get; set; }
        public DateTime Criado { get; set; }
        public DateTime Atualizado { get; set; }
        public List<GarageLeadSimulationFileDto> Arquivos { get; set; } = new();
    }

    public class GarageLeadSimulationFileDto
    {
        public Guid Id { get; set; }
        public string Nome { get; set; } = string.Empty;
        public long? Tamanho { get; set; }
        public string? ContentType { get; set; }
        public string? CaminhoRelativo { get; set; }
        public string? Url { get; set; }
        public DateTime? Data { get; set; }
    }

    public class GarageLeadStatusCountDto
    {
        public string Status { get; set; } = string.Empty;
        public int Total { get; set; }
    }

    public class GarageLeadListResponseDto
    {
        public int Pagina { get; set; }
        public int TamanhoPagina { get; set; }
        public int Total { get; set; }
        public Dictionary<string, int> TotaisPorStatus { get; set; } = new();
        public IReadOnlyList<GarageLeadListItemDto> Itens { get; set; } = Array.Empty<GarageLeadListItemDto>();
    }

    public class UpdateGarageLeadStatusRequest
    {
        public string Status { get; set; } = string.Empty;
    }

    public class UpsertGarageLeadSimulationRequest
    {
        public string Titulo { get; set; } = string.Empty;
        public string? Status { get; set; }
        public string? TipoSimulacao { get; set; }
        public string? Comentario { get; set; }
        public string? Valor { get; set; }
        public string? VeiculoMarca { get; set; }
        public string? VeiculoModelo { get; set; }
        public string? VeiculoVersao { get; set; }
        public int? VeiculoAno { get; set; }
        public int? VeiculoKm { get; set; }
        public decimal? VeiculoValor { get; set; }
        public decimal? EntradaValor { get; set; }
        public decimal? SaldoFinanciado { get; set; }
        public int? ParcelasQuantidade { get; set; }
        public decimal? ParcelaValor { get; set; }
        public decimal? TaxaJurosMensal { get; set; }
        public string? Observacoes { get; set; }
        public DateTime? ValidadeEm { get; set; }
        public int? CriadoPorUsuarioId { get; set; }
        public List<GarageLeadSimulationFileDto> Arquivos { get; set; } = new();
    }

    public class CreateGarageLeadSimulationRequest : UpsertGarageLeadSimulationRequest
    {
    }

    public class UpdateGarageLeadSimulationRequest : UpsertGarageLeadSimulationRequest
    {
    }

    public class UploadGarageLeadSimulationFileResponse
    {
        public Guid Id { get; set; }
        public string Nome { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public long Tamanho { get; set; }
        public string? ContentType { get; set; }
        public string CaminhoRelativo { get; set; } = string.Empty;
        public DateTime Data { get; set; }
    }
}
