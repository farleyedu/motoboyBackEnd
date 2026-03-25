using System.Collections.Generic;

namespace APIBack.DTOs.Permissoes
{
    public class AtualizarPermissoesVinculoRequest
    {
        public Dictionary<string, List<string>>? Permissoes { get; set; }
    }
}
