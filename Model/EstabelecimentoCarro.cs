using System;

namespace APIBack.Model
{
    public class EstabelecimentoCarro
    {
        public Guid Id { get; set; }
        public Guid MarcaId { get; set; }
        public string Marca { get; set; } = string.Empty;
        public string Modelo { get; set; } = string.Empty;
        public bool Ativo { get; set; }
    }
}
