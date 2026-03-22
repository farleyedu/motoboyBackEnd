using System;
using System.Collections.Generic;

namespace APIBack.Automation.Dtos.Estabelecimentos
{
    public class EstabelecimentoSelecionadoDto
    {
        public Guid Id { get; set; }
        public string Nome { get; set; } = string.Empty;
        public string TipoEstabelecimento { get; set; } = string.Empty;
        public List<string> ModulosAtivos { get; set; } = new();
        public string Plano { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
    }
}
