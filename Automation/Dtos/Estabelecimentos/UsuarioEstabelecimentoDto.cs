using System;
using System.Collections.Generic;

namespace APIBack.Automation.Dtos.Estabelecimentos
{
    public class UsuarioEstabelecimentoDto
    {
        public Guid EstabelecimentoId { get; set; }
        public string Nome { get; set; } = string.Empty;
        public string TipoEstabelecimento { get; set; } = string.Empty;
        public string StatusVinculo { get; set; } = string.Empty;
        public string StatusEstabelecimento { get; set; } = string.Empty;
        public bool IsAtual { get; set; }
        public bool IsSuperAdminAccess { get; set; }
        public string? TipoAcesso { get; set; }
        public List<string> ModulosAtivos { get; set; } = new();
    }
}
