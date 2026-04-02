using System;
using System.Collections.Generic;

namespace APIBack.Model
{
    public class EstabelecimentoServico
    {
        public Guid Id { get; set; }
        public Guid IdEstabelecimento { get; set; }
        public string Nome { get; set; } = string.Empty;
        public string? Descricao { get; set; }
        public string Tipo { get; set; } = string.Empty;
        public int DuracaoMinutos { get; set; } = 60;
        public long? ValorCentavos { get; set; }
        public bool Ativo { get; set; }
        public bool ExibirNoBot { get; set; } = true;
        public bool PermiteAgendamento { get; set; }
        public List<string> PalavrasChave { get; set; } = new();
        public bool DiferePorVeiculo { get; set; }
        public List<EstabelecimentoServicoVeiculoConfig> VeiculoConfigs { get; set; } = new();
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DateTime? DeletedAt { get; set; }
    }

    public class EstabelecimentoServicoVeiculoConfig
    {
        public Guid CarroId { get; set; }
        public bool Compativel { get; set; } = true;
        public long? ValorCentavos { get; set; }
        public List<EstabelecimentoServicoMarcaPeca> MarcasPeca { get; set; } = new();
    }

    public class EstabelecimentoServicoMarcaPeca
    {
        public Guid Id { get; set; }
        public string Nome { get; set; } = string.Empty;
        public long? ValorCentavos { get; set; }
    }
}
