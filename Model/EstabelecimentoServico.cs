using System;

namespace APIBack.Model
{
    public class EstabelecimentoServico
    {
        public long Id { get; set; }
        public long IdEstabelecimento { get; set; }
        public string Nome { get; set; } = string.Empty;
        public string? Descricao { get; set; }
        public string Tipo { get; set; } = string.Empty;
        public int DuracaoMinutos { get; set; }
        public decimal Valor { get; set; }
        public bool Ativo { get; set; }
        public DateTime DataCriacao { get; set; }
        public DateTime DataAtualizacao { get; set; }
    }
}
