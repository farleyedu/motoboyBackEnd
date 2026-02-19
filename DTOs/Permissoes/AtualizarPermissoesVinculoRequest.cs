using System.Collections.Generic;

namespace APIBack.DTOs.Permissoes
{
    public class AtualizarPermissoesVinculoRequest
    {
        public Dictionary<string, List<string>>? Grants { get; set; }
        public Dictionary<string, List<string>>? Revokes { get; set; }
    }
}
