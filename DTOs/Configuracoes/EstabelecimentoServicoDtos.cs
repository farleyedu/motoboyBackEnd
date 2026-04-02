using System;
using System.Collections.Generic;

namespace APIBack.DTOs.Configuracoes
{
    public class EstabelecimentoServicoDto
    {
        public Guid Id { get; set; }
        public Guid EstabelecimentoId { get; set; }
        public string Nome { get; set; } = string.Empty;
        public string? Descricao { get; set; }
        public string Tipo { get; set; } = string.Empty;
        public int DuracaoMinutos { get; set; }
        public long? ValorCentavos { get; set; }
        public bool Ativo { get; set; }
        public bool ExibirNoBot { get; set; }
        public bool PermiteAgendamento { get; set; }
        public List<string> PalavrasChave { get; set; } = new();
        public bool DiferePorVeiculo { get; set; }
        public List<ServicoVeiculoConfigDto> VeiculoConfigs { get; set; } = new();
        public long? ValorMinimoCentavos { get; set; }
        public long? ValorMaximoCentavos { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DateTime? DeletedAt { get; set; }
    }

    public class SalvarEstabelecimentoServicoRequest
    {
        public string Nome { get; set; } = string.Empty;
        public string? Descricao { get; set; }
        public string Tipo { get; set; } = string.Empty;
        public int DuracaoMinutos { get; set; }
        public long? ValorCentavos { get; set; }
        public bool Ativo { get; set; } = true;
        public bool ExibirNoBot { get; set; } = true;
        public bool PermiteAgendamento { get; set; }
        public List<string>? PalavrasChave { get; set; }
        public bool DiferePorVeiculo { get; set; }
        public List<SalvarServicoVeiculoConfigRequest>? VeiculoConfigs { get; set; }
    }

    public class AtualizarStatusRequest
    {
        public bool Ativo { get; set; }
    }

    public class ServicoVeiculoConfigDto
    {
        public string CarroId { get; set; } = string.Empty;
        public bool Compativel { get; set; }
        public long? ValorCentavos { get; set; }
        public List<MarcaPecaDto> MarcasPeca { get; set; } = new();
        public long? ValorMinimoCentavos { get; set; }
        public long? ValorMaximoCentavos { get; set; }
    }

    public class MarcaPecaDto
    {
        public string Id { get; set; } = string.Empty;
        public string Nome { get; set; } = string.Empty;
        public long? ValorCentavos { get; set; }
        public long? ValorMinimoCentavos { get; set; }
        public long? ValorMaximoCentavos { get; set; }
    }

    public class SalvarServicoVeiculoConfigRequest
    {
        public string CarroId { get; set; } = string.Empty;
        public bool Compativel { get; set; } = true;
        public long? ValorCentavos { get; set; }
        public List<SalvarMarcaPecaRequest>? MarcasPeca { get; set; }
    }

    public class SalvarMarcaPecaRequest
    {
        public string? Id { get; set; }
        public string Nome { get; set; } = string.Empty;
        public long? ValorCentavos { get; set; }
    }
}
